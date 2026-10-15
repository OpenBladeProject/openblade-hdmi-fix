<a id="openblade-hdmi-fix"></a>

# OpenBlade HDMI Fix

[English](README.md) | 简体中文 | [Русский](README.ru-RU.md)

为 Razer Blade 已保存的显示器连接应用 HDMI 临时修复。
应用会列出 Windows 记住的显示器，因此无需将显示器重新连接到笔记本，
就能修改出现问题的连接。

Patch 会将显示器声明的最大 HDMI 链路速率从 FRL10 降至 FRL8，
并保留 DSC。它会保存原始备份，以供 Restore 恢复。

<a id="requirements"></a>

## 要求

- Razer Blade 上的 Windows 11 x64。
- 曾连接过的 HDMI 显示器，且 Windows 已保存完整的功能数据。

应用会在启用 Patch 前检查已保存的 HDMI 和 DSC 字段，不限制显示器品牌或型号。
它会提示此临时修复尚未在所选显示器上验证。

<a id="use-the-app"></a>

## 使用方法

1. 保持显示器与笔记本之间的 HDMI 连接断开。
2. 从 [Releases](https://github.com/OpenBladeProject/openblade-hdmi-fix/releases)
   下载 Windows ZIP，解压后打开 `publish` 文件夹中的 **OpenBlade.HdmiFix.exe**。
   正常启动即可。
3. 选择已保存的显示器连接，查看拟应用的补丁。每条连接记录会单独列出。
4. 选择 **Patch（应用补丁）**，接受管理员权限提示，然后等待确认。
5. 重新连接显示器，检查分辨率、刷新率、HDR、VRR 和音频。

只有所选连接会被修改。更换端口、适配器或驱动后，Windows 可能创建新条目。
如果问题仍在，请断开显示器的 HDMI 连接，再检查保存的条目。
已保存数据缺失或不兼容时，Patch 不可用。

<a id="restore-and-backups"></a>

## 恢复与备份

断开显示器的 HDMI 连接，使用创建备份的 Windows 账户打开应用。
选择同一条已保存连接，再选择 **Restore（恢复）**。
确认恢复成功后再重新连接显示器。

替换应用文件夹不会删除 `%ProgramData%\OpenBlade.HdmiFix.Backups` 中的备份。
停止使用此临时修复前请保留备份。
**Backups and diagnostics（备份与诊断）**可打开该文件夹或复制报告；
报告不含原始 EDID、序列号、设备实例 ID 或本地路径。

若要调查反复黑屏，请选择出现问题的已保存连接，
在连接显示器前选择 **Record connection（记录连接）**。
每次黑屏时添加标记；记录会在 60 秒后停止。
打开记录文件夹，分享 `connection-recording.zip`。
单独保存的 Windows 显示日志可能包含私密系统数据。

请参阅[恢复](docs/zh-CN/recovery.md)、[技术细节](docs/zh-CN/technical.md)
和[构建](docs/building.md)。

<a id="license"></a>

## 许可证

[GPL-3.0-only](LICENSE)。
