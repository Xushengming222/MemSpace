# PCL 2.13.1.1 内存优化分析

分析对象：`Plain Craft Launcher 2.exe`，文件大小 5,750,272 字节，程序集及文件版本均为 2.13.1.1。

SHA-256：`30EB93C62FC51EDE0898648E67455478A3D5A4F279938AE65AEC77B84CB9703F`。

使用项目内 `analysis/InspectPcl.cs`，通过 .NET Framework 的 `ReflectionOnlyLoadFrom` 读取元数据和 IL。未启动 PCL、未执行目标方法、未修改原文件，也未在本机尝试系统级清理。部分不相关类型因缺少 MeloongCore / PCLCS 依赖而无法加载，但 `PCL.PageOtherTest.MemoryOptimizeInternal` 的完整 IL 和原生声明可读取。

## 已确认的调用

入口为 `PCL.PageOtherTest.MemoryOptimize`，核心方法为 `MemoryOptimizeInternal(Boolean)`，核心方法元数据 token 为 `0x06000a6a`。

核心方法先检查管理员身份，然后取可查询及可调整权限的当前 Windows 令牌，调用 `LookupPrivilegeValue` 和 `AdjustTokenPrivileges` 启用 `SeProfileSingleProcessPrivilege`，检查返回值和 Win32 错误。

随后循环整数操作码 **2、3、4**，每次把一个 4 字节整数传给 `ntdll.dll!NtSetSystemInformation`，信息类固定为 **80（0x50）**。每步 NTSTATUS 非零时抛出失败，成功才继续下一步。

这些枚举的对应关系与 System Informer 项目维护的 [phnt 原生接口定义](https://github.com/winsiderss/systeminformer/blob/master/phnt/include/ntexapi.h) 一致：

| 信息类 / 操作码 | 名称 | 含义 |
| --- | --- | --- |
| 信息类 80 | SystemMemoryListInformation | 系统内存列表操作 |
| 操作 2 | MemoryEmptyWorkingSets | 请求清空系统工作集 |
| 操作 3 | MemoryFlushModifiedList | 请求刷新修改页列表 |
| 操作 4 | MemoryPurgeStandbyList | 请求清空待机页列表 |

IL 中可复核的关键位置：

- `IL_004f`：权限名称 `SeProfileSingleProcessPrivilege`。
- `IL_0082`：调用 `AdjustTokenPrivileges`。
- `IL_00b6`：操作变量初始化为 2。
- `IL_00e4`：信息类参数加载 80。
- `IL_00f4`：调用 `NtSetSystemInformation`。
- `IL_0127`：操作码递增；`IL_012d` / `IL_012e` 控制循环到 4。

元数据与 IL 输出保存在项目的 `artifacts/pcl-memory-il.txt`；该输出用于本地复核，不放入 MemSpace 发布包。

## 为什么看起来释放更多

MemSpace 的普通释放按进程检查，只处理当前用户的闲置候选，并保护前台、近期使用的应用家族及有读写活动的程序。PCL 此版本直接发出系统级工作集与内存列表操作，没有在这段方法中逐个筛选进程，处理范围明显更大。

因此，“PCL 占用下降更多”与这个实现相符，但这里是依据代码的解释，未做同一负载下的实测对比，也无法保证每次释放指定容量。

清空工作集后，应用再次访问数据会触发缺页并重新载入；刷新修改页可能带来写入，清空待机页会丢弃可复用缓存。待机页本来已计入 Windows 的可用内存，清空它们主要改变空闲/缓存分布，不能把所有数字变化都算成新增可用容量。[微软工作集说明](https://learn.microsoft.com/en-us/windows/win32/memory/working-set)

## 对本工具的意义

1.2.0 已在 `src/StrongRelease.cs` 独立实现这三项系统操作，界面按钮名为“强力释放”。仅手动触发，通过 UAC 启动短时管理员助手；按 2、3、4 顺序执行，失败则停止后续步骤，恢复令牌权限，逐步返回结果及执行后 2 秒的实际可用内存变化。助手与界面通过当前用户专用的随机命名管道通信并核验进程身份。

后台释放继续使用原有进程保护策略；系统级操作无法沿用应用排除规则，界面和使用说明已明确标注。强力释放的分支与通信完成了自动测试，实际系统清理没有在开发测试中执行，未做与 PCL 的同负载容量对照，因此不能承诺每次释放 3 GB 或更多。

令牌权限的启用、完整授权检查与恢复遵循 [AdjustTokenPrivileges 文档](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-adjusttokenprivileges)。

1.3.0 根据用户要求追加“自动强力释放”复选框。它需要本次运行的管理员授权，复用经过双方进程身份校验的私有管道会话；按高占用持续 45 秒和至少 15 分钟冷却触发。关闭选项或退出主程序会结束会话。自动排除和应用名单仍只作用于后台释放，无法筛选系统级内存列表操作。

本项目未复制 PCL 的功能源码或将反编译片段打包分发。对本地 EXE 的分析只用于理解用户询问的实现方式。
