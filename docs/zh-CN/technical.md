<a id="how-the-patch-works"></a>

# 补丁原理

[English](../technical.md) | 简体中文 | [Русский](../ru-RU/technical.md)

Windows 在 `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY` 下保存显示器实例。
应用枚举这些已保存条目，包括已断开的显示器，再读取每条记录的 `Device Parameters\EDID`。
SetupAPI 的当前显示器列表用于提供连接标签。无法获取该列表时，应用会禁用修改操作。

补丁不限制显示器制造商或产品，但要求恰好一个 OUI 为 `0xC45DD8` 的 HDMI Forum VSDB、
DSC 1.2 支持，以及两个 FRL 字段都为编码 5。
它会验证 EDID 标头、制造商编码、精确扩展块数、每个 128 字节块的校验和，以及 CTA 数据块边界。
从 EDID 解码的制造商和产品必须匹配所选的已保存 Windows 实例。
每个符合条件的显示器都会显示尚未验证的警告；本应用没有经过物理验证的显示器型号。
多个 Forum 块、另一种 Forum SCDB、截断数据和非预期速率组合都会被拒绝。
两个字段均为编码 4 时，会报告已限速。

CTA 数据块偏移量为零表示没有数据块或详细时序描述符。
新补丁要求该扩展中仅有零填充，绝不将其解释为功能数据。
架构版本 1 的备份验证仅在恢复已有的精确 SAM7463 双字段覆盖时，保留旧解析器对零偏移的处理。
其他显示器备份使用当前严格解析器。
该兼容逻辑不能启用新补丁；恢复仍要求已保存 EDID 和覆盖值完全一致。

在结构化 Forum 块中，索引 7 和 12（从 CTA 块头开始计数）的高半字节由 5 改为 4。
低半字节保持不变。补丁还会重算所在扩展的校验和。
所有其他字节保持不变，包括基础 EDID、HDMI 物理地址、DSC 标志和参数、
时序、HDR、音频、VRR 及未知扩展。

字段定义已与 [Linux DRM EDID 解析器](https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/drm_edid.c)
交叉核对。这是实现参考，不是 Windows 显示稳定性的证据。

<a id="override-scope"></a>

## 覆盖范围

应用只将发生更改的扩展作为一个带编号的 128 字节 `REG_BINARY` 值，
写入所选精确实例的 `EDID_OVERRIDE`。
[Microsoft 记录了块级 EDID 覆盖机制](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/overriding-monitor-edids)；
未更改的块仍来自显示器 EDID。
已有覆盖值会被拒绝，除非它们匹配本应用已验证的恢复记录。
应用不会整体替换其他工具的覆盖值。

已保存记录不能可靠地识别物理 HDMI 端口或适配器。
应用不会因存在缓存记录就认定它证明了直连 HDMI。
不同记录使用稳定的连接标签区分，确认界面会显示所选实例。
新枚举的实例需要单独检查。
应用不会自动重新应用补丁、重启设备、重新连接或重置显卡驱动。

<a id="administrator-operation-and-recovery"></a>

## 管理员操作与恢复

WPF 窗口以普通完整性运行。Patch 和 Restore 通过 Windows UAC
启动同一可执行文件的短时子进程。
内部入口只接受固定操作、精确的 `DISPLAY\<manufacturer><product>\<instance>` 身份
（绑定到缓存 EDID）、界面显示的 EDID/覆盖指纹、Restore 使用的备份 ID，
以及发起用户的 SID。它不接受注册表根、命令或输出路径。

子进程会重新生成补丁，检查 Razer Blade 身份，再次验证连接已断开，
检查界面显示的状态，并在写入前保存持久的 `Prepared` 记录。
注册表存储层在实际修改前立即重复断开状态和预期状态检查。
命名互斥体将本应用的管理员操作串行化。
回读会先验证完整覆盖状态，再记录 `Applied`；普通权限父进程还会独立检查结果。

注册表值写入具有原子性，但 Windows 不提供跨独立覆盖工具的条件比较并设置操作。
执行 Patch 或 Restore 时，不要运行其他 EDID 编辑器或驱动更新程序。
如果出现非预期数据，应用会停止并保留原始数据。
它不会重放结果不确定的写入，也不会覆盖其他工具的值以强行恢复。

Restore 仅接受预期的补丁状态、已保存的原始状态，或应用中断留下的空注册表项。
它只移除本工具的值，绝不删除整个注册表项。
如果原始状态没有覆盖值，留下无作用的空项也视为已恢复。
出现其他值或不同 EDID 时，Restore 不可用。

备份使用原子文件替换和已刷新到存储的临时文件。
备份目录和记录必须由管理员拥有，不能有不受信任的写权限或重解析点。
负载文件只向发起操作的 Windows 账户、Administrators 和 SYSTEM 授予读权限。
其中的原始 EDID 和实例身份属于本地恢复数据，复制的诊断会省略这些信息。
进程崩溃可能留下受保护的 `.tmp` 文件。
请保留文件夹以供调查；临时文件绝不会用作恢复记录。

即使使用管理员凭据，辅助进程检查负载时仍遵守发起账户的显式读取授权。
无权读取的活动记录会阻止新补丁，并提供针对账户的解释；
它们不会禁用对当前账户自身备份的恢复。
发现记录时没有数量截断。只有新备份受 512 条活动记录的限制，
并在操作互斥体内、任何注册表修改前检查。
完成的记录会移入账户专属的 `history-` 文件夹，
其访问主体与负载相同，仍可按 ID 读取。
主文件夹中的旧版已完成记录不计入活动数量。

GPU、驱动、显示器固件和精确的笔记本变体应记录在物理验证资料中。
Razer Blade 检测是应用范围检查，不表示每个 Blade 型号都已测试。

<a id="read-only-connection-diagnostics"></a>

## 只读连接诊断

每次 Refresh 都会采集主机信息、通过 SetupAPI 获取的当前显示适配器驱动项，
以及活动的 Windows DisplayConfig 路径。
实时观察只关联完整的所选显示器实例，不按型号代码或相邻的已保存连接匹配。
Copy details 使用该快照，不会再次查询或修改设备。

HDR 来自 Windows 的 advanced-color-info 第 2 版查询。
NVIDIA VRR 来自已安装的系统 NVAPI 库，使用 DisplayConfig 适配器 LUID 和目标 ID。
功能支持、请求状态、启用状态和即时 VRR 活动分别记录。
这些字段不能证明驱动协商了哪种 HDMI FRL 速率或 DSC 模式。
缺少库、查询不受支持、拓扑不完整或映射有歧义时，相关观察会保持未知。

报告只显示已批准的描述字段和生成的状态值。
设备接口路径和适配器身份仍是私密的匹配数据；
序列号、原始 EDID、注册表路径和本机异常文本都会省略。
这些诊断读取不会改变 Patch 或 Restore 的准入条件。

无法解析的活动路径会在报告中保留受限的错误类别和数值状态。
相关匹配保持未知，同时保留独立的精确匹配结果。
主机和实时显示采集各有独立故障防护，
因此可恢复的可选读取器失败不会中止已保存显示器的发现，也不会保留过时实时观察。

<a id="recording-a-connection"></a>

## 记录连接

Record connection 会对所选完整显示器实例和活动显示拓扑采样 60 秒，通常每 500 ms 一次。
较慢的本机读取可在剩余采集时长内使用最多两秒；
采样节奏会等待该读取，而不是在 500 ms 时丢弃它。
记录内容包括 UTC 时间戳、已用时长、Windows 显示/设备通知、模式/HDR/NVIDIA VRR 观察、
查询失败和用户添加的黑屏标记。
可选的型号、固件和线缆字段会标明为用户提供的上下文。
即使选择来自断开状态的扫描，也能识别新连接且匹配的实例。
读取失败、匹配不完整或有歧义时，连接状态为未知。
其他显示器使用本次记录内的标签，不写入原始实例 ID。

时间线会逐步保存到 `%LocalAppData%\OpenBlade.HdmiFix\Recordings` 下的记录文件夹。
Stop and save 或关闭窗口会结束采样，并保留部分结果。
记录期间，Patch、Restore、Refresh 和显示器选择不可用。
记录不会更改 EDID、显示设置、驱动或设备状态。

可分享的 ZIP 包含基线报告、时间线、摘要，以及采集时段内数量受限的 Windows 事件元数据。
事件负载和消息会省略。失败或受限的查询会明确报告。
单独的 `private/display-log.dat` 由已安装的 Windows `dispdiag.exe` 收集，
可能包含可识别系统身份的数据，因此不放入 ZIP。
没有数据会自动上传。

这些观察不测量 HDMI FRL 协商、DSC 活动或实际画面。
显示器黑屏时可能没有 Windows 拓扑通知；用户标记可将可见黑屏与记录观察对应。
单凭事件 ID 无法确定某种 GPU 超时的具体原因。

<a id="read-only-scdc-observations"></a>

## 只读 SCDC 观察

Record connection 仅对 NVIDIA GPU 上所选、唯一绑定且活动的 HDMI 实例尝试读取 SCDC。
普通 Refresh 不发出 SCDC 读取。
采集前后会检查 Windows 目标/接口路径和 NVIDIA 显示映射；
无法解析、克隆、非 HDMI 或已变化的目标均返回未知观察。

读取器使用已安装在 System32 的 NVAPI 库，以及公开的显示 ID 转换和 I2C 读取接口。
每次事务从 SCDC 地址的三个固定寄存器之一读取一个字节：
Sink Version（`0x01`）、FRL 配置（`0x31`）和状态标志（`0x40`）。
组合 I2C 事务先选择寄存器地址，再读取其字节，不发送配置值。
它绝不确认更新标志、清空计数器、更改链路速率、开始训练或调用 I2CWrite。

时间线观察包括可读取的 SCDC 接收端版本、配置的 FRL 编码/通道数/速率、
通道锁定掩码和训练就绪标志。
这些只是寄存器观察，不证明视频正在传输、协商稳定或压缩正在使用。
FRL 配置编码为零也不能证明正在使用 TMDS。
实际 DSC 活动始终未知，因为公开 API 没有对应的读取方法。

寄存器和速率定义已对照
[NVIDIA HDMI 源码](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/main/src/common/sdk/nvidia/inc/ctrl/ctrl0073/ctrl0073specific.h)
与 [FRL 速率定义](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/main/src/common/inc/nvHdmiFrlCommon.h)核查。
通道锁定和训练就绪语义见
[Altera HDMI 接收端接口](https://docs.altera.com/r/docs/683798/26.1.1/hdmi-ip-user-guide/sink-interfaces?contentId=gS~IsdSbu68mvwyCbk9dpg)。
传输方式见 [NVAPI I2CRead](https://docs.nvidia.com/nvapi/group__i2capi.html)。
API 可用不保证特定驱动或显示器会开放 SCDC。
尚未验证 G95 上成功的 SCDC 读取；失败、不受支持或超时的读取保持未知，其他记录证据仍会保留。

<a id="desktop-appearance"></a>

## 桌面外观

窗口使用 OpenBlade 品牌图标和 Fluent 操作图标。
深色界面沿用紧凑的 OpenBlade 托盘仪表盘：实色 `#202020` 窗口，
WinUI 2.2.1 的深色卡片、控件、文字和焦点颜色，以及 OpenBlade 默认的 `#47E10C` 强调色。
这使配色保持一致，无需引入托盘程序依赖的 Mica 合成器。
窗口标题栏也会请求使用 Windows 深色外观。

品牌资源原样复制自 OpenBlade 核心提交 `806b0fdceaa2aa8645bcad6213bf73954939e8b2`
的 `src/OpenBlade.Tray/Assets/Brand`。
[OpenBlade 强调色管理器](https://github.com/OSSBlade/openblade-core/blob/806b0fdceaa2aa8645bcad6213bf73954939e8b2/src/OpenBlade.Tray/AccentColorManager.cs)
定义了该强调色上的绿色填充和黑色文字。
平台操作图标使用与托盘相同的 Segoe Fluent Icons 字体；可执行文件和窗口使用 `OpenBlade.ico`。

<a id="interrupted-recording-recovery"></a>

## 中断记录的恢复

记录器在报告记录已开始前，会持久保存基线和启动元数据。
每条时间线记录都会刷新到存储。
独占会话锁会在记录和最终保存期间持续持有，其他应用实例会跳过该会话。
启动时，有边界限制的恢复会检查非活动记录文件夹，并从有效的已保存数据生成独立的部分 ZIP。
它不会发出显示或 SCDC 查询、重放操作，也不会推断中断原因。

最后一条未完整写入的时间线记录会被排除。
原始时间线和任何未完成归档会保留原处。
恢复会排除未知文件、私密显示日志和用于匹配的身份标识，
并将缺失的最终证据标为不可用。它不会虚构最终样本或关闭时间戳。
突然断电时，存储设备仍可能丢失最近写入；软件刷新不能保证这些字节已保存下来。
