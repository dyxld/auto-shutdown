# 自动关机助手

**定时 / 下载完毕 / 程序任务跑完 → 电脑自动关机。** 单文件 57 KB 原生 exe，免安装、免 Python、免管理员权限（只用 Windows 自带的 .NET Framework 4.x）。

| 文件 | 用途 |
| --- | --- |
| 自动关机助手.exe | 57 KB，图形界面 + 命令行合一，双击就用 |
| AutoShutdown.cs | C# 源码（约 95 KB，单文件） |
| 重新编译.bat | 双击用系统自带 csc 编译器重新生成 exe |
| .gitignore | 忽略日志文件 |

## 三种等待方式

| 方式 | 什么时候算「到点」 |
| --- | --- |
| 定时关机 | 到你设的时间：几分钟后，或某个时刻（如 23:30，过点了算明天） |
| 下载完成 | 下载文件夹总大小连续 30 秒不变、且没有 .crdownload / .part 等临时文件 |
| 任务结束 | 程序**还开着**，但它跑的那轮任务结束了（见下节） |

结束后可选：关机 / 重启 / 休眠 / 注销 / 不操作；都有「反悔时间」（默认 60 秒）可取消。
勾上「演练模式」先空跑一遍，只显示要执行的命令，不真的关机。

## 任务结束（程序不用关）

三种信号任选，可同时开，命中任意一个就算结束：

| 信号 | 说明 | 谁适合用 |
| --- | --- | --- |
| 右下角通知 | 系统通知里出现指定的来源 / 文字就结束。读的是 Windows 通知库，所以提示被全屏游戏挡住也能检测到 | **DSH**：每轮结束都会发通知，AppID = ai.deepseek.dsh.desktop，文字是 User Turn Completed（英文界面）或 用户回合已完成（中文界面），预设两种都填好了 |
| 日志关键字 | 盯住一个日志文件，**新出现**这个词就算结束 | **MAA**：debug\asst.log 里的 AllTasksCompleted（预设会自动找到路径） |
| CPU 空闲 | 进程连续 N 秒 CPU 低于 X% 算结束（会先等它「忙起来」再计时，避免一上来就误判） | 兜底：通知被「专注助手」挡掉、或程序本身不发通知时，填 300 秒 / 3% |

图形界面：方式选「任务结束」，点「DSH 任务结束」或「MAA 任务结束」一键填好，再点开始。

命令行：

    自动关机助手.exe --task DSH --task-notify-app ai.deepseek.dsh.desktop --task-notify-text "User Turn Completed,回合已完成" --delay 60
    自动关机助手.exe --task MAA --task-log "D:/MAA/debug/asst.log" --task-log-keyword AllTasksCompleted --delay 60
    自动关机助手.exe --find-maa-log        :: 自动找 MAA 的 debug\asst.log

只认「监控开始之后」新出现的关键字，启动前日志里已有的旧记录不会误触发。

## 命令行参数

| 参数 | 说明 |
| --- | --- |
| --in MIN | 多少分钟后执行（定时） |
| --at HH:MM | 指定时刻执行（过点算明天） |
| --task NAME[,NAME] | 任务结束模式：盯住这些进程（可写简写 DSH / MAA） |
| --task-notify-app K | 右下角通知来源关键字（DSH 用 ai.deepseek.dsh.desktop） |
| --task-notify-text K[,K] | 右下角通知文字关键字，可多个（DSH 用 User Turn Completed,回合已完成） |
| --task-notify-dir DIR | 通知库目录（默认系统通知目录，仅测试用） |
| --task-log FILE | 要盯的日志文件（支持通配，取最新的） |
| --task-log-keyword K[,K] | 日志里新出现这些词就算结束（MAA 用 AllTasksCompleted） |
| --task-cpu-idle SEC | 进程 CPU 连续低于阈值 SEC 秒算结束（0 = 不用） |
| --task-cpu-percent N | CPU 空闲阈值百分比，默认 3 |
| --task-poll SEC | 任务结束检测的轮询间隔，默认 2 秒 |
| --download | 监控下载文件夹 |
| --download-dir DIR | 指定下载文件夹（默认系统「下载」夹） |
| --idle SEC | 停止变化多少秒算下载完成，默认 30 |
| --min-size MB | 至少新增多少 MB 才动作，默认 1 |
| --start-timeout SEC | 最多等多久没动静就退出，默认 1800，0 = 一直等 |
| --no-recurse | 下载文件夹不递归子目录 |
| --delay SEC | 到点后再等多少秒执行，默认 60 |
| --action | shutdown(默认) / restart / hibernate / logoff / none |
| --force | 强制结束未保存的程序 |
| --dry-run | 演练：只打印要执行的命令，不真的关机 |
| --cancel | 取消已安排的关机 |
| --detached | 后台独立运行（脱离启动它的终端 / DSH 进程树） |
| --list | 列出正在运行的程序（写进日志） |
| --find-maa-log | 自动查找 MAA 的 debug\asst.log |
| --log FILE | 指定日志文件（默认 用户目录\自动关机助手.log） |
| --selftest | 内置自检（不碰电源） |
| --gui | 打开图形界面 |

## 反悔机制

Windows 上「关机 / 重启」直接调用系统的 shutdown /s|/r /t 秒，倒计时由系统负责：

- 关掉窗口、关掉黑框，时间到了照样执行；
- 想取消：界面点「取消已安排的关机」、命令行跑 --cancel、或自己敲 shutdown /a；
- 定时模式下点「停止」也会自动取消。

休眠、注销由本程序自己等待后执行，期间不要关闭窗口。

## 安全设计

- 通知检测用「计数差值 + 只认 1~3 的小增量」：一条通知的关键字只出现 1~2 次，而通知库 checkpoint 造成的虚增是大跳，直接忽略，避免误关机；
- 读不到进程列表时只会「继续等」，绝不会当成程序已退出；
- 下载模式要同时满足「不再变化 + 无临时文件 + 新增超下限」；
- 目标名字写错时真实模式直接提示退出，不会立刻关机；
- 所有功能都能先用 --dry-run / 演练模式空跑验证。

## 自己编译

用记事本改 AutoShutdown.cs，然后双击 重新编译.bat（本质是调用系统自带编译器）：

    %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe -target:winexe -optimize+ -codepage:65001 ^
        -out:自动关机助手.exe -reference:System.dll -reference:System.Windows.Forms.dll ^
        -reference:System.Drawing.dll AutoShutdown.cs

## 常见问题

- 提示 .NET 缺失？装一个 .NET Framework 4.x（Windows 10 / 11 一般自带）。
- 名字写错了？用 --list 看准确进程名。
- 迅雷 / BT 做种中：文件一直在变会被判成「还在下载」，可先暂停下载任务。
- 测通知检测：让目标程序真的发一条通知，或临时用 --task-notify-text 指定你自己弹的通知文字。
