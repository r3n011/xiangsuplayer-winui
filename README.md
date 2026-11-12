# 像素播放器 · WinUI 3 版

基于 **WinUI 3 + Windows App SDK 2.6.5**，界面布局参照像素播放器（Android / Compose）移植，
UI 全部使用 Fluent 2 设计（Mica、NavigationView、语义画刷、卡片布局），未打包运行。

## 导航结构（与参照项目 BottomNavItem 对齐）

**主页 / 搜索 / 媒体库** ｜ 收听统计 / 均衡器 ｜ footer：设置

- 播放器不是独立页面，而是**全局底部抽屉**（对应 UnifiedPlayerSheet）：点击底部迷你播放条
  的封面/曲名区域展开全屏播放层（封面 320px、进度、传输控制、右侧同步歌词面板可开关）。
- 媒体库页使用 `SelectorBar` 六标签：**歌曲 / 专辑 / 艺术家 / 歌单 / 文件夹 / 历史**
  （对应参照项目 LibraryScreen 的 ScrollableTabRow）。

## 已实现功能

| 模块 | 说明 |
|------|------|
| 本地曲库 | TagLibSharp 扫描（标题/艺术家/专辑/流派/年份/内嵌封面），11 种格式 |
| 媒体库 | 六标签：歌曲（搜索/排序）、专辑网格、艺术家列表+详情、歌单管理、文件夹浏览、播放历史 |
| 播放引擎 | 队列、随机、单曲/列表循环、**倍速 0.25–4×**、上一首记忆、SMTC 系统媒体控件 |
| 播放历史/统计 | HistoryService 记录全部播放（history.json），「历史」标签 + **收听统计页**（总次数/时长/今日/最近7天柱状图/最常播放/最常听艺术家） |
| 主页 | 统计卡片 + 快速开始 + **每日推荐**（按日期播种）+ 最近播放 + 最多播放 |
| 搜索 | 全库搜索（曲名/专辑/艺术家/流派），分类展示艺术家/专辑/歌曲 |
| 歌词 | 同名 .lrc 同步歌词，播放抽屉内逐行高亮、点击跳转 |
| 均衡器 | **10 段 EQ（31Hz…16kHz）+ 10 个预设 + 低音增强/空间音效**（频段与参照项目一致；DSP 管线待接入） |
| 播放设置 | 倍速、变调跟随、无缝播放、交叉淡入淡出、**睡眠定时器**（到点暂停，标题栏显示截止时间） |
| 网页遥控 | **TcpListener 裸 HTTP（免管理员权限）**，手机浏览器打开 `http://局域网IP:9849` 遥控播放/音量/进度 |
| AI 集成 | 服务商/Base URL/Key/模型 配置持久化（对应 AI_INTEGRATION；云端调用随后续模块） |
| 备份恢复 | 一键导出/导入 设置+歌单+历史 JSON（FileSavePicker/FileOpenPicker） |
| 设置中心 | 分类卡片 → 详情页（播放/外观/音乐管理/均衡器/AI/网页遥控/备份/关于），分组对应 SettingsCategory |
| 主题 | 跟随系统 / 浅色 / 深色，持久化 |
| 在线搜索 | 搜索页音源切换（本地曲库/网易云/QQ/酷狗/酷我/B站/Jellyfin/Navidrome），搜索 + 直链解析流媒体播放；无版权曲目自动跳下一首 |
| 在线音源后端 | `Services/Online/*` 统一 OnlineSource 抽象 + 注册中心（直链缓存 20min）；B 站 wbi 签名；QQ 免签名 vkey 链路；`StreamProxyService` 本地转发（Referer/Cookie/Range） |
| 歌词在线获取 | 源自带歌词（网易云含翻译）→ LRCLIB 模糊匹配打分 → 内存+磁盘缓存；翻译 ±500ms 合并 |
| AI 集成 | OpenAI 兼容 + Gemini 双协议客户端（Deepseek/Groq/Kimi/GLM/OpenRouter/Ollama 预置）；AI 歌单生成（候选池+画像+XML prompt+JSON 清洗）；设置页测试连接 |
| 下载管理 | 队列 + 并发上限 + 断点续传 + 进度通知；完成后写标签/内嵌封面/.lrc 侧车 |
| 标签编辑 | TagLibSharp 读写元数据/封面（对应 SongMetadataEditor） |
| 双播放器引擎 | MediaPlayer A/B 交替：交叉淡入淡出（音量斜坡 30ms 步进）、ReplayGain 音量系数、SMTC 单活动者管理 |
| ReplayGain | 本地曲目读取 RG 标签（FLAC Vorbis comment / ID3 TXXX）+ preamp |
| 悬浮歌词 | 无边框置顶小窗，屏幕底部居中，同步显示当前歌词行 |
| Navidrome scrobble | 播放时上报 scrobble.view |

## 运行

```bash
dotnet build -c Debug -p:Platform=x64
bin/x64/Debug/net10.0-windows10.0.26100.0/PixelPlayer.WinUI.exe
```

数据：`%LocalAppData%\PixelPlayer.WinUI`（settings.json / playlists.json / library.json / history.json / covers/）。

示例数据：`tools/gen_sample_music.py`（8 首含标签封面的 WAV + LRC）、`tools/fix_wav_tags.py`、
截图工具 `tools/shot.py`（支持指定窗口尺寸、恢复最小化窗口）。

## 移植中（按参照项目模块）

EQ DSP 实际生效（AudioGraph + IBasicAudioEffect，unpackaged 下需独立 WinRT 组件工程）、
LX JS 音源脚本引擎（QuickJS 对应）、Telegram（TDLib）、一起听（网易云 eapi 轮询协议）、
听歌识曲（网易云/酷狗指纹）、Chromecast、Android Auto/Wear、MCP 服务器、主题插件商店、
MusicBrainz 元数据补全、电台/排行榜、AI TTS/网页搜索、备份对齐 PXPL 分模块格式。

## 已踩过的坑（重要）

1. XAML 里给 Slider 赋 `Value` + 绑 `ValueChanged` → 解析期闪退；事件一律代码挂。
2. 静态字段必须先于 `static Instance` 声明，否则单例构造时字段为 null。
3. 后台线程操作 UI 绑定的 ObservableCollection / 创建 BitmapImage → 原生崩溃（无托管异常）；
   必须经 `DispatcherQueue` 调度。
4. **ScrollViewer 内元素设 MaxWidth（任意 alignment）→ 内容右移 ~400px 且右侧裁切**（WinUI 3 布局怪癖）。
   列表页用 Grid 根节点 + 默认 Stretch；ScrollViewer 页面不要设 MaxWidth。
5. `{Binding}` 对元组字段不可靠：绑定用条目请用具名类属性。
6. HttpListener 监听 `http://+:port` 需要管理员 ACL；网页遥控改用 TcpListener 裸 HTTP。
7. XAML 里控件设 `SelectedIndex="0"` 会在 InitializeComponent 解析期就触发 `SelectionChanged`，
   此时其后声明的命名控件（x:Name）字段还是 null → NRE 闪退；事件回调里必须判空。
8. KEYEVENTF_UNICODE 的 SendInput 可用于驱动 UI 测试输入，`tools/ui.py`（ctypes 实现，含归一化坐标点击）。
