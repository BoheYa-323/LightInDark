using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;    // ClassInjector：IL2CPP 下 AddComponent 前必须先注册类型
using Light.UI.MainMenu;                  // GradientButton —— 自绘「淡金描边 + 半透明深色底」按钮
using Light.UI.Window;                    // MenuTextTemplate2（简中字体）/ VanillaAsset（白图 / 音效）
using LightInDark.Core;                   // LightLogger
using LightInDark.UI.Window;              // LayerExpansion
using TMPro;
using UnityEngine;
using UnityEngine.Events;                 // UnityAction（给原版 PassiveButton 挂监听用）
using UnityEngine.SceneManagement;        // 判断当前场景，决定窗口挂谁下面
using MetaScreen = Light.UI.HudUI.MetaScreen;   // ⚠️ Light.UI.Window 里也有个 MetaScreen，必须用别名消歧

namespace Light.UI.MusicPlayer;

/// <summary>
/// **音乐播放器窗口** —— F3 开关，任何场景都能用。
///
/// ═══════════════════════════════════════════════════════════════════════
///  【这一版为什么推翻重写】
///
///  上一版是 **Canvas（Screen Space Overlay）+ 自己算鼠标命中**，实机三个问题全中：
///    ① 中文完全不显示（自己 <c>AddComponent&lt;TextMeshPro&gt;</c> 新建 TMP，字体没接上）；
///    ② 按钮位置偏（Canvas 参考分辨率换算，实机分辨率和 1920×1080 不一致就偏）；
///    ③ 点击穿透到游戏自己的按钮（自绘命中判定拦不住原版 <c>PassiveButtonManager</c>）。
///
///  现在改成**和「更换背景图」面板（BackgroundPanel）完全相同的那一套**
///  —— 世界空间 Quad 自绘 + 原版 MetaScreen 窗口 + GradientButton + MenuTextTemplate2，
///  它在本工程已经实机验证过：字体没问题、位置由 UI 层坐标直接决定（不会因为分辨率偏）、
///  点击由原版自身的 <c>PassiveButton</c> 处理（和游戏按钮同一套输入链路）。
///
///  ═══════════════════════════════════════════════════════════════════════
///  【抄自 BackgroundPanel 的具体做法】（每一条都是踩过坑才定下来的）
///
///  · **窗口** —— <c>Light.UI.HudUI.MetaScreen.GenerateWindow(...)</c>，
///    <c>background: Modern</c>、<c>withCloseButton: true</c>、<c>sortingGroupOrder: 300</c>，
///    再把 MetaWindow 的 <c>localScale</c> 缩到 <see cref="PanelScale"/>（内容坐标一个字不用改）。
///  · **所有色块 / 边框 / 分隔线** —— <c>SpriteRenderer</c> + <c>drawMode = Sliced</c> + <c>size</c> 拉伸，
///    贴图取 <c>VanillaAsset.FullScreenSprite</c>（自建白图）或
///    <c>GradientButton.PanelSprite</c>（自绘圆角九宫格，永不失效）。
///  · **层** —— 每个 <c>new GameObject</c> 都设 <c>LayerExpansion.GetUILayer()</c>（= layer 5）。
///    这是"大厅里看不见"的根因：layer 0 归 Main Camera（depth −1），会被 UI Camera（depth 99）画的东西整个盖住。
///  · **同一容器内 z 越小越靠前** —— 底 +0.05、边框 +0.03、文字/按钮 −0.10。
///  · **整窗压过其它 UI** —— MetaScreen 会给 MetaWindow 挂 <c>SortingGroup(order = 300)</c>。
///  · **点击不穿透** —— <see cref="UiModalGuard"/>：每帧把"不属于本窗口"的原版控件临时禁用 +
///    <c>InstallOutsideClickGuard</c> 在窗口**矩形之外**铺四条带状碰撞盒（见下方注释）。
///  · **字体** —— <c>MenuTextTemplate2.Font / FontMaterial</c>（NotoSansSC），
///    并且**建完之后再统一刷一遍**（<see cref="ApplyCjkFontToAll"/>），双保险。
///  · **不写任何 Harmony 补丁** —— F3 在本组件 <see cref="Update"/> 里轮询。
///
///  ═══════════════════════════════════════════════════════════════════════
///  【布局】（用户授权自定；见类末尾的 ASCII 图）
/// <code>
/// ┌──────────────────────────────────────────────────────────────┐
/// │ 音乐播放器                  正在播放xxx  1:00 / 2:30         │
/// │ ──────────────────────────────────────────────────────────── │
/// │ ┌──────────────┐ ┃ ┌──────────────────────────────────────┐   │
/// │ │打开配置文件夹 │ ┃ │ 01. 曲目一                            │   │
/// │ │  刷新列表     │ ┃ │ 02. 曲目二                            │   │
/// │ │ 曲库：N 首    │ ┃ │ …（8 行，一页正好一屏）                │   │
/// │ │ 把歌曲放进    │ ┃ └──────────────────────────────────────┘   │
/// │ │ Music 文件夹  │ ┃  [上一页]   第 1 / 1 页   [下一页]          │
/// │ │ 支持 mp3/…    │ ┃  [暂停][播放][下一首]   [ 列表循环 ]        │
/// │ │[−]音量 70%[+] │ ┃                                            │
/// │ │[━━━━━●───────]│ ┃                                            │
/// │ │  关闭窗口     │ ┃                                            │
/// │ └──────────────┘ ┃                                            │
/// └──────────────────────────────────────────────────────────────┘
/// </code>
///
///  ⚠️ 2026-10-05 二次调整（用户要求）：
///    · 删掉了左栏原来那行 **「按 F3 也可开关窗口」**（F3 本来就能开关，这行既多余、字又小）；
///    · 腾出来的位置放**音量控件**：一行读数（`音量 70%` + 左右各一个 `-` / `+` 步进按钮）
///      + 一行**可拖动的音量条**（占满左栏宽度 2.45）。
///    · 音量条是**自绘**的，不是原版设置界面那条 —— 为什么，见
///      <see cref="BuildVolumeControl"/> 的长注释（结论：原版 <c>SlideBar</c> 拿模板要满场景找
///      非单例的 <c>OptionsMenuBehaviour</c>、克隆体会带上写游戏音量的持久化监听、
///      而且它的拖动换算是"世界坐标差当 localPosition"与缩过的窗口对不上 → 不稳妥）。
///
///  ⚠️ 2026-10-05 另外两条追加需求（同一位用户，一起做）：
///    · **右上角状态行加时间进度** —— `未在播放` / `正在播放{曲名}  1:00 / 2:30`，
///      数据来自新增的 <c>MusicPlayer.Position / Duration</c>（见 <see cref="BuildStatus"/>）。
///    · **播放器在播 + 玩家在主界面 → 压住原版主界面 BGM**（`Ambience/MainMenuBgMusic`），
///      随时可还原（见 <see cref="TickMainMenuBgm"/> 的长注释，那里写了为什么没照抄
///      <c>BackgroundRenderer.MuteVanillaMusic</c> 的全局 mixer 通道做法）。
///
///  ⚠️ 控制行（<see cref="CtrlY"/>）现在是**四个中文文字按钮**：
///     `暂停` / `播放` / `下一首` / 循环模式（`随机播放`|`列表循环`|`单曲循环`）。
///     2026-10-05 用户要求：**不要 Unicode 符号**（▶ ❚❚ 在 NotoSansSC 里字形覆盖不稳，
///     用户原话"那个按钮标签文本看起来是真不行"），全部改汉字。
///     宽度按"每个全角字 ≈ 0.517 × fontSize"算（ModeBtnWidth=3.00 时 1.45 号四个字正好排满），
///     合计 6.07 ≤ 右栏宽度 6.10（见 <see cref="CtrlRowWidthUsed"/>）。
/// </summary>
public sealed class MusicPlayerWindow : MonoBehaviour
{
    // =====================================================================
    //  布局常量（单位 = 窗口本地坐标；MetaWindow 自身再乘 PanelScale）
    //    坐标原点在窗口中心，+x 向右、+y 向上。
    // =====================================================================

    /// <summary>窗口尺寸（和 BackgroundPanel 的 10.0×6.6 同一量级，缩小后不会超出屏幕）。</summary>
    private static readonly Vector2 WindowSize = new(9.2f, 5.2f);

    /// <summary>整体缩放：窗口底图按 <see cref="WindowSize"/> 建，缩 MetaWindow 即可等比缩小全部内容。</summary>
    private const float PanelScale = 0.82f;

    /// <summary>可视行数。**故意等于 <see cref="MusicLibrary.PageSize"/>** —— 一页正好一屏，不会漏歌。</summary>
    private const int VisibleRows = MusicLibrary.PageSize;

    private const float RowWidth = 5.85f;
    private const float RowHeight = 0.30f;
    private const float RowPitch = 0.33f;      // 行距（行高 + 0.03 间隙）
    private const float RowFirstY = 1.76f;     // 第一行的中心 y（往下排）

    private const float ListCenterX = 1.35f;   // 右栏（列表 + 控制条）中心 x
    private static readonly Vector2 ListSize = new(6.10f, 2.80f);
    private const float ListCenterY = 0.60f;

    private const float LeftCenterX = -3.22f;  // 左栏中心 x
    private const float LeftBtnWidth = 2.45f;
    private const float LeftBtnHeight = 0.46f;

    // ===== 音量控件（2026-10-05 新增；占的是原来「按 F3 也可开关窗口」那一行的位置）=====
    //
    //  【纵向怎么排得下】用本工程实测的排版约定「一行文字的高度 ≈ 0.41 × 字号」算（见下方注释）：
    //      上一行提示 HintFmt（0.75 号、2 行）的**字底** ≈ -0.86 − 0.41×0.75×2/2 = -1.1675
    //      「关闭窗口」按钮**顶边** = -2.15 + 0.46/2 = -1.92
    //      → 可用高度 = 0.7525
    //    两行控件：读数行 0.95 号（高 0.3895）+ 滑条抓取区 0.30 = 0.6895 ≤ 0.7525 ✓ 不重叠、不溢出。
    //    （读数行还要装 ± 按钮，但按钮只有 0.32 高，比读数行本身矮 → 不额外占高度。）
    //
    //  【为什么滑条占满 2.45 全宽】左栏本来就只有 2.45 宽，拖动是靠"鼠标 x → 0~1"映射的，
    //    越宽越好拖（像素分辨率越高）。所以把 ± 按钮挪到上面那行和读数并排、左右对称，
    //    滑条自己独占一整行。
    private const float VolumeFontSize = 1.20f;         // 音量读数「音量 70%」的字号（比被删掉的 0.75 提示行大）
    private const float VolumeValueY = -1.43f;          // 读数 + ± 按钮那一行的中心 y
    private const float VolumeStepBtnSize = 0.32f;      // ± 按钮边长（正方形；与 ButtonColorPanel 里 0.5×0.34 的翻页按钮同量级）
    private const float VolumeStepBtnGap = 0.07f;       // ± 按钮 ↔ 读数 之间的间隙
    private const float VolumeBarY = -1.76f;            // 滑条（槽 / 填充 / 手柄 / 抓取区）的中心 y
    private const float VolumeBarWidth = LeftBtnWidth;  // 滑条宽度 = 左栏宽度 2.45
    private const float VolumeHitHeight = 0.30f;        // 抓取区高度（**不可见**；底边 -1.91，正好不压到关闭按钮的 -1.92）
    private const float VolumeGrooveHeight = 0.18f;     // 槽（可见）
    private const float VolumeFillHeight = 0.12f;       // 填充条（可见）
    private const float VolumeKnobWidth = 0.13f;        // 手柄
    private const float VolumeKnobHeight = 0.24f;
    private const float VolumeStep = 0.05f;             // ± 每点一次 5%

    /// <summary>读数字的可用宽度（左右各让出一个 ± 按钮 + 间隙）：2.45 − 2×(0.32+0.07) = 1.67。</summary>
    private const float VolumeValueWidth = LeftBtnWidth - (VolumeStepBtnSize + VolumeStepBtnGap) * 2f;

    private const float NavY = -1.12f;         // 「上一页 / 页码 / 下一页」那一行
    private const float NavBtnWidth = 1.35f;
    private const float NavBtnHeight = 0.40f;

    // ===== 控制行（CtrlY）：暂停 / 播放 / 下一首 / 循环模式 =====
    //
    //  ⚠️⚠️ 字号与宽度的换算依据（**已实测的比例，别乱改**）：
    //     ModeBtnWidth 原来是 3.00，fontSize=1.45 时「列表循环」四个全角字**正好排满**
    //     → 每个全角字的宽度 ≈ 0.517 × fontSize。
    //     （TMP 那边还有自动缩放兜底：fontSizeMin = fontSize × 0.65，
    //       GradientButton 的文字矩形宽 = size.x × 0.94。所以每处都留了余量。）
    //
    //  【右栏可用宽度】navLeft = ListCenterX − ListSize.x/2 = −1.70，右边缘 = +4.40，
    //   共 6.10。四个按钮的总占宽必须 ≤ 6.10。
    //
    //  【这一版怎么排的】控制行字号统一 0.92：
    //     暂停  = 2×0.517×0.92 + 0.12 内边距 = 1.08
    //     播放  = 1.08
    //     下一首= 3×0.517×0.92 + 0.12       = 1.56
    //     按钮间距 0.10 × 3                = 0.30
    //     小计                             = 4.02   → 循环模式还剩 6.10−4.02 = 2.08
    //     循环模式 = 2.05（四个字：4×0.517×0.92 + 0.14 = 2.04，取 2.05）
    //     合计     = 1.08+1.08+1.56+0.30+2.05 = **6.07 ≤ 6.10** ✓
    //   三个新按钮从 navLeft 起算靠左排，循环模式按钮**仍然右对齐到 4.40**
    //   （和原来一样的对齐方式），中间自然留出 0.13 的缝 → 不会重叠、不会溢出。
    private const float CtrlY = -1.95f;        // 播放/暂停/下一首/循环模式那一行
    private const float CtrlFontSize = 1.02f;  // 控制行统一字号（四个按钮都用它）
    private const float CtrlGap = 0.10f;       // 三个新按钮之间的间距
    private const float CtrlBtnHeight = 0.64f; // 控制行四个按钮统一高度
    private const float PauseBtnWidth = 1.08f;
    private const float PlayBtnWidth = 1.08f;
    private const float NextBtnWidth = 1.56f;
    private const float ModeBtnWidth = 2.05f;  // 循环模式（原来是 3.00，腾地方给新按钮）
    private const float ModeBtnHeight = CtrlBtnHeight;

    /// <summary>控制行实际占用的总宽度（构建时打进日志，方便核对"没超 6.10"）。</summary>
    private const float CtrlRowWidthUsed =
        PauseBtnWidth + CtrlGap + PlayBtnWidth + CtrlGap + NextBtnWidth + CtrlGap + ModeBtnWidth;

    /// <summary>竖分隔线的 x（左栏 / 右栏分界）。</summary>
    private const float DividerX = -1.85f;

    /// <summary>关闭按钮缩放（HudUI 写死 0.57，太小；和 BackgroundPanel 一样放大到 0.95）。</summary>
    private const float CloseButtonScale = 0.95f;

    /// <summary>每多少帧强制做一次完整的模态扫描（见 <see cref="Update"/> 里的说明）。</summary>
    private const int ForceSweepEvery = 20;

    /// <summary>场景切换后最多重试多少帧去重建窗口（父物体可能还没就绪）。</summary>
    private const int MaxRebuildTries = 300;

    // ===== 配色（用户要求：主色调淡金）=====
    private static readonly Color PaleGold = new(0.96f, 0.84f, 0.55f, 1f);          // 淡金（描边/高亮/符号）
    private static readonly Color PaleGoldDim = new(0.86f, 0.74f, 0.48f, 1f);       // 暗一点的淡金
    private static readonly Color BorderGlow = new(1f, 0.95f, 0.85f, 0.75f);        // 辉光白（分隔线）
    private static readonly Color GoldLine = new(0.96f, 0.84f, 0.55f, 0.55f);       // 淡金分隔线
    private static readonly Color PanelBg = new(0.11f, 0.10f, 0.09f, 0.92f);        // 列表底（深色衬金）
    private static readonly Color CtrlBg = new(0.11f, 0.10f, 0.09f, 0.75f);         // 控制条底
    private static readonly Color VolGroove = new(0.18f, 0.17f, 0.15f, 0.95f);      // 音量槽底（比窗口底略亮，空槽也看得见）
    private static readonly Color TextMain = new(1f, 0.95f, 0.85f, 1f);             // 辉光白
    private static readonly Color TextDim = new(0.78f, 0.75f, 0.72f, 1f);           // 次要文字
    private static readonly Color TextWarn = new(1f, 0.62f, 0.55f, 1f);             // 出错

    // =====================================================================
    //  静态状态（F3 轮询宿主）
    // =====================================================================

    private static MusicPlayerWindow? _instance;

    /// <summary>当前是否**正在显示**（诊断 / 外部判断用）。</summary>
    public static bool IsOpen
    {
        get
        {
            try
            {
                var i = _instance;
                return i != null && i._showing && i.IsAlive;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 幂等创建：重复调用**不会**建出第二份。
    ///
    /// 挂在 <see cref="MusicPlayer"/> 那个 <c>DontDestroyOnLoad</c> 的宿主下面 →
    /// 本组件活过所有场景，这就是"模组加载完之后随时能按 F3"的基础。
    /// （注意：**窗口本身**是场景物体，挂在 HudManager / MainMenuManager 下面，
    ///   切场景会被销毁 → 由 <see cref="Update"/> 在新场景里重建，见那里的注释。）
    /// </summary>
    public static MusicPlayerWindow? TryCreate(Transform? parent)
    {
        try
        {
            if (_instance != null) return _instance;

            // IL2CPP：类型必须先注册，否则 AddComponent 会抛
            try { ClassInjector.RegisterTypeInIl2Cpp<MusicPlayerWindow>(); }
            catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayerWindow] 注册类型失败（可能已注册）: {ex.Message}"); }

            var go = new GameObject("LID_MusicPlayerWindow");
            go.hideFlags = HideFlags.HideAndDontSave;
            if (parent != null) go.transform.SetParent(parent, false);

            _instance = go.AddComponent<MusicPlayerWindow>();
            return _instance;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.TryCreate]", ex);
            return null;
        }
    }

    // =====================================================================
    //  实例状态
    // =====================================================================

    private MetaScreen? _screen;
    private GameObject? _windowObj;

    private MusicPlayer? _player;   // ⚠️ 别再用 global:: 限定：MusicPlayer 类就在 Light.UI.MusicPlayer 命名空间里，加限定反而解析不到

    private bool _built;
    private bool _showing;

    private TextMeshPro? _titleText;
    private TextMeshPro? _statusText;
    private TextMeshPro? _libText;
    private TextMeshPro? _emptyText;
    private TextMeshPro? _pageText;

    private GradientButton? _playButton;
    private GradientButton? _pauseButton;
    private GradientButton? _nextButton;
    private GradientButton? _modeButton;
    private GradientButton? _closeButton;

    // ===== 音量控件（自绘；见 BuildVolumeControl）=====
    private TextMeshPro? _volumeText;          // 「音量 70%」
    private SpriteRenderer? _volBarFill;       // 填充条（宽度跟着音量变）
    private SpriteRenderer? _volKnob;          // 手柄（位置跟着音量变）
    private Collider2D? _volBarHit;            // 不可见的抓取区：拖动靠它的 OverlapPoint 命中
    private bool _volDragging;                 // 正在拖音量条（松手/关窗都要复位）
    private Camera? _volDragCam;               // 按下时锁定的那台相机（拖动期间不改，免得数值跳）

    // ===== 追加需求：播放器在播 + 主界面 → 压住原版主界面 BGM（见 TickMainMenuBgm）=====
    private AudioSource? _menuBgm;             // 抓到的原版主界面 BGM（Ambience/MainMenuBgMusic 上的音源）
    private bool _bgmMuted;                    // 我们此刻是否压着它（true = 已经静音）
    private float _bgmSavedVolume;             // 压之前它自己的 volume（原样记下，还原时写回）
    private bool _bgmSavedMute;                // 压之前它自己的 mute
    private bool _bgmSavedPlaying;             // 压之前它是否在播（**只记不写回**，理由见 RestoreMainMenuBgm）
    private int _bgmTick;                      // 低频节流计数
    private bool _bgmLoggedMissing;            // "找不到 BGM" 只记一次，免得每帧刷屏

    /// <summary>主界面 BGM 的同步频率：每多少帧判一次（10 帧 ≈ 6Hz，够跟手了，省掉每帧的场景名 interop 开销）。</summary>
    private const int BgmSyncEvery = 10;

    private readonly List<GradientButton> _rowButtons = new();
    private readonly List<int> _rowTracks = new();      // 与 _rowButtons 一一对应：该行对应的全局曲目下标（-1 = 空行）

    private int _guardTick;
    private int _rebuildTries;
    private bool _loggedParentMissing;

    // ===== 控制行的按钮文字 =====
    //
    //  ⚠️⚠️ 2026-10-05 用户要求：**按钮标签一律汉字，不要 Unicode 符号**。
    //    原因：`▶` / `❚❚` 这类符号在任何一款中文字体里都不保证有字形，
    //    实测在 NotoSansSC-Regular SDF 上显示效果"真不行"（缺字形 → 显示成方框/极细/错位）。
    //    → 删掉了原来的 `ResolveGlyphs()` 字形探测那一整套（PlayCandidates/PauseCandidates/PickGlyph）。
    //
    //  ⚠️ `_playGlyph` / `_pauseGlyph` 这两个字段**保留**（构建日志和按钮文字都在用它们），
    //    只是现在恒等于中文标签 —— 不留悬空引用，也不再有"探测失败退到 ASCII |> / ||"的分支。
    private const string PlayLabel = "播放";
    private const string PauseLabel = "暂停";
    private const string NextLabel = "下一首";

    private string _playGlyph = PlayLabel;
    private string _pauseGlyph = PauseLabel;

    /// <summary>窗口对象是否还活着（Unity 假 null 判定：场景切换会把它销毁）。</summary>
    private bool IsAlive
    {
        get
        {
            try { return _windowObj != null; }
            catch { return false; }
        }
    }

    /// <summary>当前是否显示中（外部只读）。</summary>
    public bool IsShowing
    {
        get
        {
            try { return _showing && IsAlive; }
            catch { return false; }
        }
    }

    /// <summary>兜底：宿主被销毁时把模态登记摘掉（正常退出游戏时走一次），并把被压住的主界面 BGM 还原。</summary>
    private void OnDestroy()
    {
        try
        {
            if (_windowObj != null) UiModalGuard.Pop(_windowObj.transform);
            _showing = false;
        }
        catch { }

        // 本组件要没了 → 绝对不能把原版 BGM 留在"被我们静音"的状态里
        try { RestoreMainMenuBgm(); } catch { }
    }

    // =====================================================================
    //  每帧
    // =====================================================================

    private void Update()
    {
        // ⚠️ Update 里绝不能让异常逃出去（会中断 MonoBehaviour 消息链），每一段单独 try/catch
        try
        {
            // ① F3 开关（任何场景都在轮询；输入系统抽风也不影响游戏）
            try
            {
                if (Input.GetKeyDown(KeyCode.F3)) Toggle();
            }
            catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayerWindow] F3 轮询失败: {ex.Message}"); }

            // ①.5 原版主界面 BGM 的压制/还原（追加需求）。
            //      ⚠️⚠️ 必须在下面 `if (!_showing) return;` **之前**：
            //      "播放器暂停 / 放完 / 离开 MainMenu" 都可能在**窗口关着**的时候发生，
            //      那时同样得把 BGM 还原 —— 否则 BGM 会被永久压住（用户明确要求"必须能恢复"）。
            try { TickMainMenuBgm(); }
            catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayerWindow] 主界面 BGM 同步失败: {ex.Message}"); }

            if (!_showing) return;

            // ② 窗口是**场景物体**：切场景时被连根销毁 → 在新场景里重建，
            //    这样"开着窗口进游戏 / 回主菜单"窗口不会凭空消失。
            if (!IsAlive)
            {
                if (_rebuildTries++ < MaxRebuildTries) Rebuild();
                else
                {
                    _showing = false;
                    LightLogger.LogWarning("[MusicPlayerWindow] 新场景里找不到可挂载的父物体，窗口已关闭（再按 F3 可重开）");
                }
                return;
            }

            // ③ 模态遮罩：把"不属于本窗口"的原版控件临时禁用（见 UiModalGuard 的长注释：
            //    原版点击判定**完全不看 z**，铺一层碰撞盒根本拦不住，只能让它们当帧失效）。
            //    ⚠️ 主菜单里 MainMenuPatch.LateUpdate 会调 Sweep，但**大厅 / 游戏内没有别人驱动它**，
            //       所以这里必须自己驱动 —— 否则"点击穿透"只会在主菜单里看起来是好的。
            try { UiModalGuard.Sweep(); } catch { }

            // Sweep 内部有"Buttons 数量没变就短路"的优化，而原版会自己把按钮 enabled 回来
            // （大厅 HUD 按钮随状态开关很常见）→ 低频强制全扫一次兜底。
            if (++_guardTick >= ForceSweepEvery)
            {
                _guardTick = 0;
                try { UiModalGuard.ForceSweep(); } catch { }
            }

            // ③.5 音量条拖动：每帧轮询鼠标（本工程既有做法，见 TickVolumeDrag 注释）。
            //      放在 RefreshDynamic **之前**，这样这一帧拖出来的值当帧就能画出来。
            try { TickVolumeDrag(); }
            catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayerWindow] 音量轮询失败: {ex.Message}"); }

            // ④ 动态文字 / 高亮
            RefreshDynamic();
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.Update] {ex.Message}");
        }
    }

    // =====================================================================
    //  开关
    // =====================================================================

    /// <summary>F3：打开 ↔ 关闭（同一个键，幂等）。</summary>
    public void Toggle()
    {
        try
        {
            if (_showing && IsAlive) Hide();
            else Show();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.Toggle]", ex);
        }
    }

    /// <summary>打开窗口（首次会构建；场景切换后会自动重建）。</summary>
    public void Show()
    {
        try
        {
            // 幂等：原版按钮一次点击可能被触发多次，重复 Show 会让遮罩登记和界面状态错乱
            if (_showing && IsAlive) return;

            _player = MusicPlayer.Instance;

            if (!_built || !IsAlive) Build();
            if (_windowObj == null) return;      // 建不出来（场景没就绪）→ 什么都不做

            _windowObj.SetActive(true);
            _showing = true;
            _rebuildTries = 0;

            UiModalGuard.Push(_windowObj.transform);

            // 每次打开顺手重扫一次目录：刚丢进 Music 的歌立刻可见。
            // ⚠️ ReloadLibrary 只重建列表、不打断正在播的那首（见 MusicPlayer 的注释）。
            try { _player?.ReloadLibrary(); } catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayerWindow] 重扫曲库失败: {ex.Message}"); }

            RefreshList();
            ApplyCjkFontToAll();     // 再刷一遍字体（第一次构建时若字体还没解析出来，这里补上）

            LightLogger.Log($"[MusicPlayerWindow] 已打开（曲库 {(_player != null ? _player.Library.Count : 0)} 首，{UiModalGuard.Describe()}）");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.Show]", ex);
        }
    }

    /// <summary>
    /// 关闭窗口。只 <c>SetActive(false)</c>，**不销毁** —— 下次打开更快，也不会留下野引用
    /// （真正销毁它的是场景切换，那种情况由 <see cref="Update"/> 负责重建）。
    /// </summary>
    public void Hide()
    {
        try
        {
            _showing = false;
            _volDragging = false;      // 关窗就停止拖动（否则下次开窗会"接着上次的按住状态"继续拖）
            _volDragCam = null;
            if (_windowObj != null)
            {
                // ⚠️ 先摘遮罩登记再隐藏：Pop 会把被我们临时禁用的原版控件全部还原
                UiModalGuard.Pop(_windowObj.transform);
                _windowObj.SetActive(false);
            }
            LightLogger.Log("[MusicPlayerWindow] 已关闭");
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.Hide] {ex.Message}");
        }

        // 关窗时**强制重判一次**主界面 BGM（用户把"窗口关闭"也列进了恢复时机）。
        // ⚠️ 注意：关窗 **不等于** 停止播放 —— 窗口只是 UI，音乐还在放（那是 MusicPlayer 的常驻宿主）。
        //    所以这里不是"无条件还原"，而是"立刻按真实条件重判"：
        //      · 音乐已停/已暂停 → 立刻还原 BGM ✓（这正是用户要的效果）
        //      · 音乐还在放     → 继续保持静音（否则会和正在放的歌叠在一起，与主需求相悖）
        try { TickMainMenuBgm(force: true); }
        catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayerWindow] 关窗时同步 BGM 失败: {ex.Message}"); }
    }

    /// <summary>场景切换后重建窗口（旧窗口已经随场景销毁）。</summary>
    private void Rebuild()
    {
        try
        {
            DestroyPartial();      // 清残骸 + 摘遮罩登记 + 清所有引用
            Build();
            if (_windowObj == null) return;    // 父物体还没就绪 → 下一帧继续试（有次数上限）

            _windowObj.SetActive(true);
            UiModalGuard.Push(_windowObj.transform);
            _rebuildTries = 0;
            _player = MusicPlayer.Instance;
            RefreshList();

            LightLogger.Log($"[MusicPlayerWindow] 场景切换后已重建窗口（场景={SafeSceneName()}）");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.Rebuild]", ex);
        }
    }

    // =====================================================================
    //  构建
    // =====================================================================

    private void Build()
    {
        if (_built && IsAlive) return;
        DestroyPartial();

        var parent = ResolveParent();
        if (parent == null)
        {
            // 只在第一次记一行，避免场景过渡期间刷屏
            if (!_loggedParentMissing)
            {
                _loggedParentMissing = true;
                LightLogger.LogWarning($"[MusicPlayerWindow] 场景 {SafeSceneName()} 里没有可挂载的父物体（HudManager / MainMenuManager 都没就绪），本次不建窗口");
            }
            return;
        }

        try
        {
            // ⚠️ sortingGroupOrder 必须够高：整窗会被 MetaScreen 挂上一个 SortingGroup，
            //    它相当于"整窗合成一个渲染器"，order 低于别人就会被别人整个盖住
            //    （其它模组窗口默认 100；「更换背景图」用 300，这里同值）。
            _screen = MetaScreen.GenerateWindow(
                WindowSize, parent, new Vector3(0f, 0f, -40f),
                withBlackScreen: true,
                // 自己装"点窗口**以外**才关窗"（HudUI 那块整屏 ClickGuard 会把窗口内部的空白也吃掉，
                // 表现为"点窗口里任何非按钮的地方都关窗"），见 InstallOutsideClickGuard
                closeOnClickOutside: false,
                background: Light.UI.HudUI.BackgroundSetting.Modern,
                withCloseButton: true,
                sortingGroupOrder: 300);

            if (_screen == null) { LightLogger.LogWarning("[MusicPlayerWindow] MetaScreen.GenerateWindow 失败"); return; }

            var win = _screen.transform.parent;
            if (win == null) { LightLogger.LogWarning("[MusicPlayerWindow] 窗口没有父物体"); return; }
            _windowObj = win.gameObject;

            // 整体缩小：窗口底图是按 WindowSize 建的，缩 MetaWindow 会让边框/遮罩/内容等比缩小，
            // 内容坐标一个字都不用改（BackgroundPanel 同款做法）。
            _windowObj.transform.localScale = Vector3.one * PanelScale;

            BrightenWindowChrome();
            RewireCloseButton();

            // 点**窗口矩形之外**才关窗：四条带状碰撞盒拼起来把窗口挖空。
            // ⚠️ 不能用 HudUI 那块 100×100 的整屏 ClickGuard —— 原版点击判定不看 z，
            //    它在窗口内部也会命中（见 UiModalGuard 的注释）。
            UiModalGuard.InstallOutsideClickGuard(_windowObj, WindowSize, () =>
            {
                try { Hide(); }
                catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayerWindow] 点外部关窗失败: {ex.Message}"); }
            });

            var root = _screen.transform;

            // ⚠️ 这里原来先调 ResolveGlyphs() 挑 ▶ / ❚❚ 字形；已整段删除
            //    （按钮文字现在恒为汉字「暂停 / 播放 / 下一首」，见 PlayLabel 那一带的注释）。
            BuildHeader(root);
            BuildLeftColumn(root);
            BuildRightColumn(root);

            ApplyCjkFontToAll();

            _built = true;
            _loggedParentMissing = false;

            var font = Light.UI.Window.MenuTextTemplate2.Font;
            LightLogger.Log($"[MusicPlayerWindow] 界面已构建：窗口 {WindowSize.x}×{WindowSize.y} ×{PanelScale}" +
                            $"（视觉 {WindowSize.x * PanelScale:F2}×{WindowSize.y * PanelScale:F2}），" +
                            $"行 {VisibleRows}×{RowHeight}，父物体={SafeName(parent.name)}，场景={SafeSceneName()}，" +
                            $"字体={(font != null ? font.name : "未解析")}，控制行文字=暂停/播放/下一首");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.Build]", ex);
            DestroyPartial();     // 半成品不要留着（否则下次会误判成"已经建好了"）
            _built = false;
        }
    }

    /// <summary>销毁建了一半/已失效的窗口并清空所有引用。</summary>
    private void DestroyPartial()
    {
        try
        {
            if (_windowObj != null)
            {
                try { UiModalGuard.Pop(_windowObj.transform); } catch { }
                UnityEngine.Object.Destroy(_windowObj);
            }
        }
        catch { }

        _windowObj = null;
        _screen = null;

        _titleText = null;
        _statusText = null;
        _libText = null;
        _emptyText = null;
        _pageText = null;
        _playButton = null;
        _pauseButton = null;
        _nextButton = null;
        _modeButton = null;
        _closeButton = null;

        _volumeText = null;
        _volBarFill = null;
        _volKnob = null;
        _volBarHit = null;
        _volDragging = false;
        _volDragCam = null;

        _rowButtons.Clear();
        _rowTracks.Clear();

        _built = false;
    }

    /// <summary>
    /// 决定窗口挂谁下面。
    ///
    /// ⚠️ 本窗口必须**任何场景都能开**，而各场景的"UI 根"不是一个东西：
    ///    · 大厅 / 游戏内 → <c>HudManager</c>（和 HudUI 的窗口同一套，实测在 UI 层、被 UI 相机会画到最前）；
    ///    · 主菜单 / 匹配中 → <c>MainMenuManager</c>（和「更换背景图」面板同一套）。
    ///   挂错地方会表现为"窗口完全不出现"（layer / 相机不匹配，见 AGENTS.md §4.3）。
    /// </summary>
    private static Transform? ResolveParent()
    {
        try
        {
            var scene = SafeSceneName();

            if (scene == "MainMenu" || scene == "MatchMaking")
            {
                var menu = DestroyableSingleton<MainMenuManager>.Instance;
                if (menu != null) return menu.transform;
            }

            var hud = HudManager.Instance;
            if (hud != null) return hud.transform;

            var menu2 = DestroyableSingleton<MainMenuManager>.Instance;
            if (menu2 != null) return menu2.transform;

            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.ResolveParent] {ex.Message}");
            return null;
        }
    }

    private static string SafeSceneName()
    {
        try { return SceneManager.GetActiveScene().name ?? "?"; }
        catch { return "?"; }
    }

    // =====================================================================
    //  窗口装饰
    // =====================================================================

    /// <summary>
    /// 把窗口"底"调亮一点。
    ///
    /// <c>GenerateScreen</c> 里 BlackScreen 的 alpha 是写死的 <b>0.4226</b>，
    /// 叠上偏暗的面板底，整块界面发黑。这里把遮罩调淡（同 BackgroundPanel）。
    /// </summary>
    private void BrightenWindowChrome()
    {
        try
        {
            if (_windowObj == null) return;
            var t = _windowObj.transform;

            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c == null) continue;

                if (c.name == "BlackScreen")
                {
                    var sr = c.GetComponent<SpriteRenderer>();
                    if (sr != null) sr.color = new Color(0f, 0f, 0f, 0.24f);
                }
                else if (c.name == "Inner")
                {
                    // Modern 风格的窗口底（原版九宫格图），原来被乘了 0.55 → 太暗
                    var sr = c.GetComponent<SpriteRenderer>();
                    if (sr != null) sr.color = Color.white.RGBMultiplied(0.82f);
                }
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.BrightenWindowChrome] {ex.Message}");
        }
    }

    /// <summary>
    /// 把 HudUI 默认的关闭动作（<c>Object.Destroy(窗口)</c>）换成 <see cref="Hide"/>。
    ///
    /// ⚠️ 必须整体替换 <c>OnClick</c>（不是追加）：HudUI 挂的是 Destroy，
    ///    那会让 <c>_showing</c> / 遮罩登记和实际状态不一致（下次 F3 会走进"重建"分支）。
    /// ⚠️ 替换时会连它挂的点击音效一起清掉，所以自己要补回来（BackgroundPanel 踩过这个）。
    /// </summary>
    private void RewireCloseButton()
    {
        try
        {
            if (_windowObj == null) return;
            var t = _windowObj.transform;

            Transform? close = null;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c != null && c.name == "CloseButton") { close = c; break; }
            }
            if (close == null) return;

            close.localScale = Vector3.one * CloseButtonScale;

            var pb = close.GetComponent<PassiveButton>();
            if (pb == null) return;

            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            pb.OnClick.AddListener((UnityAction)(() => PlaySound("UI_Select", 0.8f)));
            pb.OnClick.AddListener((UnityAction)(() => Hide()));

            if (pb.OnMouseOver != null)
                pb.OnMouseOver.AddListener((UnityAction)(() => PlaySound("UI_Hover", 0.7f)));
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.RewireCloseButton] {ex.Message}");
        }
    }

    // =====================================================================
    //  界面各区块
    // =====================================================================

    private void BuildHeader(Transform root)
    {
        try
        {
            // ⚠️ 左对齐的 TMP 是从矩形**左边缘**开始画的 → 这里传的 x 是"想要的左边缘"
            //    （MakeText 内部会换算成矩形中心）
            // y=2.26：标题行是 1.6 号字（本工程实测行高 ≈ 0.41×字号 ≈ 0.66），
            // 窗口顶边 2.6、下方横线 2.02 —— 正好卡在中间，不会压线也不会顶出窗口。
            _titleText = MakeText(root, "Title", "音乐播放器", new Vector3(-4.25f, 2.26f, -0.1f),
                1.60f, FontStyles.Bold, TextAlignmentOptions.Left, TextMain, width: 5.0f);

            // 右上角状态（未在播放 / 正在播放{曲名}  1:00 / 2:30 / 已暂停 / 加载中 / 出错）
            // ⚠️ 歌名/错误信息都按"全角宽度预算"截断（见 Shorten）：右对齐文字变长是往左长的，
            //    不截断会一路顶到左边的标题上。
            // 2026-10-05 追加时间进度后整行更长了 → 再加两道保险：
            //   · 自动缩放（0.70~1.00）：这一行的字宽比例离线算不准，让它自己缩到装得下；
            //   · 溢出省略号：连最小字号都装不下时截成 "…"，而不是硬撑着压到标题上。
            _statusText = MakeText(root, "Status", "未在播放", new Vector3(4.25f, 2.26f, -0.1f),
                1.25f, FontStyles.Normal, TextAlignmentOptions.Right, TextDim, width: 5.4f);

            if (_statusText != null)
            {
                _statusText.enableAutoSizing = true;
                _statusText.fontSizeMax = 1.25f;
                _statusText.fontSizeMin = 0.90f;
                _statusText.enableWordWrapping = false;
                _statusText.overflowMode = TextOverflowModes.Ellipsis;
            }

            // 顶部横线 + 中间竖线（淡金主色）
            MakeLine(root, new Vector3(0f, 2.02f, -0.05f), new Vector2(8.70f, 0.022f), BorderGlow);
            MakeLine(root, new Vector3(DividerX, -0.22f, -0.05f), new Vector2(0.022f, 4.44f), GoldLine);
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.BuildHeader]", ex);
        }
    }

    // =====================================================================
    //  背景音共存开关（用户 2026-10-05 追加）
    // =====================================================================

    /// <summary>背景视频音轨是否与音乐**共存**。false（默认）= 放歌时把背景视频静音。</summary>
    private bool _coexist;

    private GradientButton? _coexistButton;

    /// <summary>
    /// 放在左栏原「支持 mp3/wav/flac」那一行的位置（中心 y = -1.05，高 0.42）。
    /// 纵向预算：本行 [-1.26, -0.84]，上面 HintDir 底 -0.70 ✓、下面音量读数顶 -1.184 ✓。
    /// </summary>
    private void BuildCoexistButton(Transform root)
    {
        try
        {
            _coexistButton = MakeButton(root, "CoexistBtn", CoexistLabel(),
                new Vector2(LeftBtnWidth, 0.42f), new Vector2(LeftCenterX, -1.05f),
                OnCoexistClick, 1.05f, _coexist);

            ApplyCoexist();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.BuildCoexistButton]", ex);
        }
    }

    /// <summary>按钮上的字。只有这两个短语，用户一眼能看出当前状态。</summary>
    private string CoexistLabel() => _coexist ? "背景音共存：开" : "背景音共存：关";

    private void OnCoexistClick()
    {
        try
        {
            _coexist = !_coexist;
            ApplyCoexist();
            LightLogger.Log($"[MusicPlayerWindow] 背景音共存已切换为 {(_coexist ? "开" : "关")}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.OnCoexistClick]", ex);
        }
    }

    /// <summary>
    /// 把开关状态落到 <see cref="Light.UI.MainMenu.BackgroundRenderer.ExternalMute"/>。
    ///
    /// ⚠️ 是**取反**：共存 = 开 → 不静音背景；共存 = 关 → 静音背景视频音轨。
    /// ⚠️ 只压**视频音轨**，不动画面；也不碰原版主界面 BGM（那条走 TickMainMenuBgm）。
    /// </summary>
    private void ApplyCoexist()
    {
        try
        {
            // ⚠️⚠️ **必须带上"是否真的在放歌"**（2026-10-05 用户报的 bug）。
            //
            //   原来这里写的是 `ExternalMute = !_coexist;` —— 而 ApplyCoexist() 在
            //   **建窗口时就会被调一次**，于是"一按 F3 背景音就没了"，跟放不放歌无关。
            //   用户的原意是：**F3 播放音乐时**才按共存开关处理，没放歌时背景音照常。
            //
            //   所以条件 = 不共存 && 正在播放。播放状态由 RefreshDynamic() 每帧同步过来
            //   （见那里对 ApplyCoexist 的调用），暂停/停止/放完会自动恢复。
            bool playing = _player != null && _player.IsPlaying;
            Light.UI.MainMenu.BackgroundRenderer.ExternalMute = !_coexist && playing;

            if (_coexistButton != null)
            {
                _coexistButton.SetText(CoexistLabel());
                _coexistButton.SetSelected(_coexist);
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.ApplyCoexist] {ex.Message}");
        }
    }

    private void BuildLeftColumn(Transform root)
    {
        try
        {
            // ---- 两个功能按钮（用户要求的左区）----
            // 字号 1.10：最长的「打开配置文件夹」是 7 个全角字，1.20 号会顶满按钮宽度（太挤），
            // 1.10 号下 7 字 ≈ 2.23，按钮内宽 2.45 —— 两边各留一点白，看着舒服。
            MakeButton(root, "OpenFolder", "打开配置文件夹",
                new Vector2(LeftBtnWidth, LeftBtnHeight), new Vector2(LeftCenterX, 1.55f),
                OnOpenFolder, 1.10f);

            MakeButton(root, "RefreshList", "刷新列表",
                new Vector2(LeftBtnWidth, LeftBtnHeight), new Vector2(LeftCenterX, 0.98f),
                OnRefreshList, 1.10f);

            // ---- 曲库统计 / 提示 ----
            // y 的间距是按"行高 ≈ 0.41×字号"排的（见 BuildHeader 的说明），保证互不重叠。
            _libText = MakeText(root, "LibInfo", "曲库：0 首", new Vector3(LeftCenterX, 0.45f, -0.1f),
                1.20f, FontStyles.Normal, TextAlignmentOptions.Center, PaleGoldDim, width: 2.6f);

            // 字号 1.05（原来 0.85，用户说太小）。位置和矩形高度同步放大，
            // 纵向预算：本行中心 -0.25、高 0.90 → 占 [-0.70, 0.20]；
            //           上一行 LibInfo 中心 0.45、字号 1.20 → 占 [0.204, 0.696] ✓ 不重叠。
            MakeText(root, "HintDir", "把歌曲放进\nMusic 文件夹",
                new Vector3(LeftCenterX, -0.25f, -0.1f),
                1.05f, FontStyles.Normal, TextAlignmentOptions.Center, TextDim,
                width: 2.60f, height: 0.90f);

            // ⚠️ 2026-10-05 用户要求"字还是小" → **删掉原「支持 mp3/wav/flac」那一行**
            //    （0.75 号字最小、信息量最低），腾出的位置放**背景音共存开关**。
            //    格式说明并没有丢 —— 它仍然在 `MusicClipLoader.FormatHint` 里，
            //    并且选中不支持的格式时列表会在 _emptyText 上给出提示。
            BuildCoexistButton(root);

            // ⚠️ 2026-10-05 用户要求：这里原来那行「按 F3 也可开关窗口」**已删除**
            //    （F3 本来就能开关，这行纯属多余，而且 0.75 号字用户嫌小）。
            //    腾出来的位置改放音量控件 —— 见 BuildVolumeControl。
            BuildVolumeControl(root);

            // ---- 关闭窗口（鼠标也能关；关闭按钮太小/在窗口外面，给一个明确的）----
            _closeButton = MakeButton(root, "CloseWindow", "关闭窗口",
                new Vector2(LeftBtnWidth, LeftBtnHeight), new Vector2(LeftCenterX, -2.15f),
                Hide, 1.10f);
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.BuildLeftColumn]", ex);
        }
    }

    /// <summary>
    /// **音量控件**：读数（`音量 70%`）+ 左右两个 `-` / `+` 步进按钮 + 一条可拖动的音量条。
    ///
    /// 位置 = 原来那行「按 F3 也可开关窗口」腾出来的地方（纵向预算见 VolumeFontSize 那一带的注释）。
    ///
    /// ═══════════════════════════════════════════════════════════════════
    ///  【为什么是自绘，而不是复用原版设置界面那条音量滑条】
    ///
    ///  先查了 AU 19.0 反编译源码：原版设置界面的音量条是
    ///  <c>SlideBar : PassiveUiElement</c>（字段 <c>OptionsMenuBehaviour.MusicSlider</c> /
    ///  <c>SoundSlider</c>，见 OptionsMenuBehaviour.cs 第 173/179/266/267/298/301 行；
    ///  <c>SlideBar</c> 本身在 SlideBar.cs，靠 <c>ReceiveClickDrag</c> + <c>PassiveButtonManager</c>
    ///  的拖拽状态机工作）。**结论：不能稳妥复用**，三条硬理由：
    ///
    ///   ① **拿不到模板**：<c>OptionsMenuBehaviour</c> 不是 <c>DestroyableSingleton</c>
    ///      （就是个普通 MonoBehaviour），要拿它只能 <c>FindObjectOfType</c> 满场景找，
    ///      而且它在大厅 / 游戏内 / 主菜单的存在性与 active 状态都不保证
    ///      —— 等于把"音量条能不能显示"绑在"原版设置菜单此刻在不在场"上。
    ///
    ///   ② **克隆体会带上原版的持久化监听**：预制体里的 <c>OnValueChange</c> 是序列化好的
    ///      UnityEvent，指向 <c>OptionsMenuBehaviour.UpdateMusicVolume</c> →
    ///      拖我们的音量条会**顺手改掉游戏自己的音乐音量设置（还会写盘）**。
    ///      必须整体替换 <c>OnValueChange</c>（同 AGENTS.md §4.5「克隆后必须替换 OnClick」的坑）。
    ///
    ///   ③ **它的换算和我们的窗口对不上**：<c>SlideBar.ReceiveClickDrag</c> 是
    ///      <c>vector = PassiveButtonManager...controller.DragPosition - Bar.transform.position</c>
    ///      （**世界坐标差**）然后直接当 <c>Dot.transform.localPosition</c> 用。
    ///      本窗口整体缩了 <see cref="PanelScale"/>(0.82) 倍，世界差 ≠ 本地差 →
    ///      手柄位置和鼠标必然对不齐（提前满格 / 拖不到头）。想对齐得反推原版 Bar 的尺寸、
    ///      Range、缩放三级关系，非常脆。
    ///
    ///  → 按用户"**如果不行就算了**"的授权，这里自绘。做法全部用本工程已验证过的件：
    ///     · 槽底 / 手柄 —— <c>GradientButton.PanelSprite</c>（自绘圆角九宫格，已打 DontUnloadUnusedAsset，
    ///       不会像原版贴图那样在场景卸载后变成"假 null"）；
    ///     · 填充条 —— <c>VanillaAsset.FullScreenSprite</c>（自建 1×1 白图 + Sliced 拉伸）；
    ///     · 拖动 —— 和 <c>GradientButton.PollRightClick</c> / <c>BackgroundPanel.TickInput</c>
    ///       同一套"每帧读 <c>Input</c> + <c>Collider2D.OverlapPoint</c> 命中"，
    ///       **完全绕开原版 <c>PassiveButtonManager</c>**：它那套拖拽只发给注册过的
    ///       <c>PassiveUiElement</c>，而且点击是"每个控件各判一次、不看 z"（见 UiModalGuard 的长注释），
    ///       我们的滑条不是原版控件，挤进去只会和原版控件抢同一次按下。
    /// ═══════════════════════════════════════════════════════════════════
    /// </summary>
    private void BuildVolumeControl(Transform root)
    {
        try
        {
            float left = LeftCenterX - LeftBtnWidth * 0.5f;    // 左栏左边缘 = -4.445
            float right = LeftCenterX + LeftBtnWidth * 0.5f;   // 左栏右边缘 = -1.995

            // ---- ① 读数「音量 70%」：夹在两个 ± 按钮中间，整体仍在左栏正中 ----
            _volumeText = MakeText(root, "VolumeText", FormatVolume(MusicPlayer.DefaultVolume),
                new Vector3(LeftCenterX, VolumeValueY, -0.1f),
                VolumeFontSize, FontStyles.Normal, TextAlignmentOptions.Center, PaleGold,
                width: VolumeValueWidth);

            if (_volumeText != null)
            {
                // ⚠️ 开自动缩放兜底：本工程注释里"每个全角字 ≈ 0.517 × 字号"和
                //    "1.10 号下 7 字 ≈ 2.23"(≈0.29 × 字号) 两种说法对不上，
                //    离线算不准真机比例。开了自动缩放后，**无论哪种比例**读数都不会溢出
                //    这 1.67 宽的格子（上限 0.95，下限 0.57 仍比被删掉的 0.75 提示行好认）。
                _volumeText.enableAutoSizing = true;
                _volumeText.fontSizeMax = VolumeFontSize;
                _volumeText.fontSizeMin = VolumeFontSize * 0.60f;
                _volumeText.enableWordWrapping = false;
            }

            // ---- ② ± 步进（拖动是连续的，这两个负责"精确一格"）----
            // 用 ASCII 的 '-' / '+'：NotoSansSC 一定有字形，不会像 ▶/❚❚ 那样缺字（见文件头的说明）。
            MakeButton(root, "VolumeDown", "-",
                new Vector2(VolumeStepBtnSize, VolumeStepBtnSize),
                new Vector2(left + VolumeStepBtnSize * 0.5f, VolumeValueY), OnVolumeDown, 1.00f);

            MakeButton(root, "VolumeUp", "+",
                new Vector2(VolumeStepBtnSize, VolumeStepBtnSize),
                new Vector2(right - VolumeStepBtnSize * 0.5f, VolumeValueY), OnVolumeUp, 1.00f);

            // ---- ③ 滑条三层：槽(底) → 填充条 → 手柄(最前) ----
            // z 按本文件约定"同一容器内 z 越小越靠前"：槽 +0.05、填充 +0.04、手柄 +0.02。
            MakePanel(root, "VolGroove", new Vector2(LeftCenterX, VolumeBarY),
                new Vector2(VolumeBarWidth, VolumeGrooveHeight), VolGroove, 0.05f);

            _volBarFill = MakeSolidRect(root, "VolFill",
                new Vector3(LeftCenterX, VolumeBarY, 0.04f),
                new Vector2(VolumeBarWidth, VolumeFillHeight), PaleGold);

            _volKnob = MakePanel(root, "VolKnob", new Vector2(LeftCenterX, VolumeBarY),
                new Vector2(VolumeKnobWidth, VolumeKnobHeight), TextMain, 0.02f);

            // ---- ④ 不可见的抓取区：拖动靠它的 OverlapPoint 做命中 ----
            // 高度 0.30（底边 -1.91）→ 正好贴住「关闭窗口」按钮顶边(-1.92)但**不重叠**，
            // 免得点关闭按钮时被判定成"开始拖音量"。
            var hit = new GameObject("VolBarHit");
            hit.layer = LayerExpansion.GetUILayer();
            hit.transform.SetParent(root, false);
            hit.transform.localPosition = new Vector3(LeftCenterX, VolumeBarY, 0f);

            var col = hit.AddComponent<BoxCollider2D>();
            col.isTrigger = true;
            col.size = new Vector2(VolumeBarWidth, VolumeHitHeight);
            _volBarHit = col;

            // 初值：按播放核心当前的音量画（不是硬编码 0.7，重启/重建后读数才对得上）
            EnsurePlayer();
            RefreshVolumeVisual(_player != null ? _player.Volume : MusicPlayer.DefaultVolume);

            LightLogger.Log($"[MusicPlayerWindow] 音量控件已构建（自绘）：读数 y={VolumeValueY} 字号={VolumeFontSize}" +
                            $"（自动缩放 {VolumeFontSize * 0.60f:F2}~{VolumeFontSize:F2}）宽={VolumeValueWidth:F2}，" +
                            $"± 按钮 {VolumeStepBtnSize}×{VolumeStepBtnSize} 步进 {VolumeStep * 100f:F0}%，" +
                            $"滑条 y={VolumeBarY} 宽={VolumeBarWidth:F2}（槽高 {VolumeGrooveHeight}，抓取高 {VolumeHitHeight}）");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.BuildVolumeControl]", ex);
        }
    }

    private void BuildRightColumn(Transform root)
    {
        try
        {
            // ---- 列表底 + 淡金描边 ----
            // ⚠️ 用 GradientButton.PanelSprite（**自绘**的圆角九宫格）而不是原版弹窗底图：
            //    原版资源会在场景卸载/UnloadUnusedAssets 后变成"假 null" → "框没了、字还在"。
            MakePanel(root, "ListBg", new Vector2(ListCenterX, ListCenterY), ListSize, PanelBg, 0.05f);
            AddGoldBorder(root, new Vector3(ListCenterX, ListCenterY, 0.05f), ListSize, 0.035f);

            // ---- 歌曲行（8 行 = 一页；行按钮复用，不重复创建）----
            for (int i = 0; i < VisibleRows; i++)
            {
                int idx = i;
                var btn = MakeButton(root, $"Row{idx}", "",
                    new Vector2(RowWidth, RowHeight),
                    new Vector2(ListCenterX, RowFirstY - idx * RowPitch),
                    () => OnRowClicked(idx), 1.10f);
                if (btn == null) continue;      // 建失败就别塞进列表，否则后面刷新会 NRE

                // 行文字改成"左对齐 + 左边留白"：按钮自带的文字是居中并把 rect 撑满的，
                // 这里把 rect 收窄并右移一点，让歌名从头开始排（序号列对齐）。
                try
                {
                    btn.Text.alignment = TextAlignmentOptions.Left;
                    btn.Text.enableWordWrapping = false;
                    btn.Text.rectTransform.sizeDelta = new Vector2(RowWidth - 0.70f, RowHeight * 1.1f);
                    btn.Text.rectTransform.localPosition = new Vector3(-0.19f, 0f, -0.1f);
                }
                catch { }

                _rowButtons.Add(btn);
                _rowTracks.Add(-1);
            }

            // ---- 空列表提示 ----
            _emptyText = MakeText(root, "EmptyHint",
                "Music 文件夹里还没有歌曲\n" + MusicClipLoader.FormatHint +
                "\n\n点左边「打开配置文件夹」把歌丢进去，\n再点「刷新列表」",
                new Vector3(ListCenterX, ListCenterY, -0.2f),
                0.95f, FontStyles.Normal, TextAlignmentOptions.Center, TextDim,
                width: ListSize.x - 0.4f, height: ListSize.y - 0.3f, wrap: true);
            if (_emptyText != null) _emptyText.gameObject.SetActive(false);

            // ---- 翻页：三条在右栏里等距排开 ----
            float navLeft = ListCenterX - ListSize.x * 0.5f;     // 右栏左边缘
            MakeButton(root, "PrevPage", "上一页",
                new Vector2(NavBtnWidth, NavBtnHeight),
                new Vector2(navLeft + NavBtnWidth * 0.5f, NavY), OnPrevPage, 1.05f);

            _pageText = MakeText(root, "PageText", "第 1 / 1 页", new Vector3(ListCenterX, NavY, -0.1f),
                1.00f, FontStyles.Normal, TextAlignmentOptions.Center, PaleGold, width: 2.30f);

            MakeButton(root, "NextPage", "下一页",
                new Vector2(NavBtnWidth, NavBtnHeight),
                new Vector2(navLeft + ListSize.x - NavBtnWidth * 0.5f, NavY), OnNextPage, 1.05f);

            // ---- 控制条：暂停 / 播放 / 下一首 + 循环模式 ----
            //  ⚠️ 文字**全部是汉字**（用户要求，不要 Unicode 符号）；宽度换算依据见
            //     PauseBtnWidth 那一带的常量注释（每个全角字 ≈ 0.517 × fontSize）。
            MakePanel(root, "CtrlBg",
                new Vector2(ListCenterX, CtrlY), new Vector2(ListSize.x, 1.00f), CtrlBg, 0.05f);

            // 三个新按钮从 navLeft（右栏左边缘）起算，逐个往右排
            float pauseX = navLeft + PauseBtnWidth * 0.5f;
            float playX = pauseX + PauseBtnWidth * 0.5f + CtrlGap + PlayBtnWidth * 0.5f;
            float nextX = playX + PlayBtnWidth * 0.5f + CtrlGap + NextBtnWidth * 0.5f;

            _pauseButton = MakeButton(root, "PauseBtn", _pauseGlyph,
                new Vector2(PauseBtnWidth, CtrlBtnHeight),
                new Vector2(pauseX, CtrlY), OnPauseClick, CtrlFontSize);

            _playButton = MakeButton(root, "PlayBtn", _playGlyph,
                new Vector2(PlayBtnWidth, CtrlBtnHeight),
                new Vector2(playX, CtrlY), OnPlayClick, CtrlFontSize);

            _nextButton = MakeButton(root, "NextBtn", NextLabel,
                new Vector2(NextBtnWidth, CtrlBtnHeight),
                new Vector2(nextX, CtrlY), OnNextClick, CtrlFontSize);

            // 循环模式：按钮上的文字**就是那四个字本身**（列表循环 / 单曲循环 / 随机播放），
            // 位置**保持原来的"右对齐到右栏右边缘"**，只有宽度从 3.00 收到 2.05 给新按钮腾地方。
            _modeButton = MakeButton(root, "LoopMode", MusicPlayer.LoopModeLabel(MusicLoopMode.ListLoop),
                new Vector2(ModeBtnWidth, ModeBtnHeight),
                new Vector2(navLeft + ListSize.x - ModeBtnWidth * 0.5f, CtrlY), OnLoopModeClick, CtrlFontSize);

            // 自检日志：宽度加起来有没有超出右栏（6.10）。超出就是布局 bug，日志里一眼能看到。
            float nextRight = nextX + NextBtnWidth * 0.5f;
            float modeLeft = navLeft + ListSize.x - ModeBtnWidth;
            LightLogger.Log($"[MusicPlayerWindow] 控制行布局：文字={_pauseGlyph}/{_playGlyph}/{NextLabel} " +
                            $"字号={CtrlFontSize} 占宽={CtrlRowWidthUsed:F2}/{ListSize.x:F2}｜" +
                            $"暂停 x={pauseX:F2} 宽={PauseBtnWidth:F2}，" +
                            $"播放 x={playX:F2} 宽={PlayBtnWidth:F2}，" +
                            $"下一首 x={nextX:F2} 宽={NextBtnWidth:F2}（右边缘 {nextRight:F2}），" +
                            $"循环模式 x={navLeft + ListSize.x - ModeBtnWidth * 0.5f:F2} 宽={ModeBtnWidth:F2}" +
                            $"（左边缘 {modeLeft:F2}，间隙 {modeLeft - nextRight:F2}）" +
                            $"{(CtrlRowWidthUsed > ListSize.x ? " ⚠️ 溢出！" : " ✓ 未溢出")}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.BuildRightColumn]", ex);
        }
    }

    // =====================================================================
    //  控件工厂（写法与 BackgroundPanel 一致）
    // =====================================================================

    /// <summary>
    /// 建一段文字。
    ///
    /// ⚠️ 关键：TMP 是**按矩形边缘**对齐的 —— 左对齐时文字从矩形**左边缘**开始画，
    ///    所以 <paramref name="pos"/>.x 传的是"矩形中心"。为方便调用，这里做了换算：
    ///    左/右对齐时你传的是**想要的边缘**，内部再换算成矩形中心（同 BackgroundPanel.MakeText）。
    ///
    /// ⚠️ 字体必须先设 <c>font</c> 再设 <c>fontSharedMaterial</c>：
    ///    字形来自字体自己的图集材质，只写 font 会继续用旧材质 → 字看不见/乱码。
    /// </summary>
    private static TextMeshPro? MakeText(Transform parent, string name, string text, Vector3 pos,
        float fontSize, FontStyles style, TextAlignmentOptions align, Color color,
        float width = 0f, float height = 0f, bool wrap = false)
    {
        try
        {
            var obj = new GameObject(name);
            obj.layer = LayerExpansion.GetUILayer();     // ⚠️ 不设层 = layer 0 = 被 UI 相机画的东西盖住
            obj.transform.SetParent(parent, false);
            obj.transform.localScale = Vector3.one;

            var tmp = obj.AddComponent<TextMeshPro>();
            ApplyCjkFont(tmp);

            if (width > 0f || height > 0f)
                tmp.rectTransform.sizeDelta = new Vector2(width > 0f ? width : 100f,
                                                          height > 0f ? height : 100f);

            // 左/右对齐：把"想要的边缘"换算成矩形中心
            if (width > 0f)
            {
                if (align is TextAlignmentOptions.Left) pos.x += width * 0.5f;
                else if (align is TextAlignmentOptions.Right) pos.x -= width * 0.5f;
            }

            obj.transform.localPosition = pos;

            tmp.text = text;
            tmp.alignment = align;              // Left/Center/Right 都是"垂直居中"，不用再换算 y
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = color;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = wrap;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.ForceMeshUpdate();
            return tmp;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.MakeText]", ex);
            return null;
        }
    }

    /// <summary>建一个自绘按钮（<see cref="GradientButton"/>：淡金渐变描边 + 半透明深色底）。</summary>
    private static GradientButton? MakeButton(Transform parent, string name, string text, Vector2 size,
        Vector2 center, Action onClick, float fontSize, bool selected = false)
    {
        try
        {
            var btn = GradientButton.Create(parent, text, size, onClick, selected, fontSize);
            if (btn == null) return null;

            btn.GameObject.name = name;
            btn.SetPosition(new Vector3(center.x, center.y, -0.10f));   // 按钮整体压在底/边框之上
            return btn;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.MakeButton]", ex);
            return null;
        }
    }

    /// <summary>一条色块（分隔线 / 边框条）。用白图 + Sliced 拉伸，永远死不了。</summary>
    private static void MakeLine(Transform parent, Vector3 pos, Vector2 size, Color color)
        => MakeSolidRect(parent, "Line", pos, size, color);

    /// <summary>
    /// 一块**纯色矩形**（<c>VanillaAsset.FullScreenSprite</c> = 自建 1×1 白图，Sliced 拉伸成任意尺寸）。
    /// 返回渲染器是为了让调用方**每帧改 size / 位置**（音量填充条就是这么用的）。
    ///
    /// ⚠️ 用自建白图而不是原版贴图：原版资源场景一卸载就变成 Unity 假 null →
    ///    "框没了、字还在"（AGENTS.md §4.6）。自建白图由 VanillaAsset 自己持有并打了
    ///    DontUnloadUnusedAsset，永远不会失效。
    /// </summary>
    private static SpriteRenderer? MakeSolidRect(Transform parent, string name, Vector3 pos,
        Vector2 size, Color color)
    {
        try
        {
            var obj = new GameObject(name);
            obj.layer = LayerExpansion.GetUILayer();
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = pos;

            var sr = obj.AddComponent<SpriteRenderer>();
            sr.sprite = Light.UI.Window.VanillaAsset.FullScreenSprite;   // = 自建 1×1 白图
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.tileMode = SpriteTileMode.Continuous;
            sr.size = size;
            sr.color = color;
            return sr;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.MakeSolidRect] {name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>一块面板底（圆角九宫格，自绘贴图 → 不受场景卸载影响）。</summary>
    private static SpriteRenderer? MakePanel(Transform parent, string name, Vector2 center,
        Vector2 size, Color color, float z)
    {
        try
        {
            var obj = new GameObject(name);
            obj.layer = LayerExpansion.GetUILayer();
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = new Vector3(center.x, center.y, z);

            var sr = obj.AddComponent<SpriteRenderer>();

            // GradientButton.PanelSprite 是它自绘的圆角九宫格（已打 DontUnloadUnusedAsset）
            var spr = GradientButton.PanelSprite;
            if (spr == null) spr = Light.UI.Window.VanillaAsset.FallbackPanelSprite;
            if (spr == null) spr = Light.UI.Window.VanillaAsset.WhiteSprite;

            sr.sprite = spr;
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.tileMode = SpriteTileMode.Continuous;
            sr.size = size;
            sr.color = color;
            return sr;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.MakePanel]", ex);
            return null;
        }
    }

    /// <summary>给一块面板加一圈淡金描边（四条细条拼的，不用贴图）。</summary>
    private static void AddGoldBorder(Transform parent, Vector3 center, Vector2 size, float thickness)
    {
        try
        {
            float z = center.z - 0.02f;     // 比底略靠前（同容器内 z 越小越靠前）
            float hx = size.x * 0.5f, hy = size.y * 0.5f;

            MakeLine(parent, new Vector3(center.x, center.y + hy, z), new Vector2(size.x + thickness * 2f, thickness), PaleGold);
            MakeLine(parent, new Vector3(center.x, center.y - hy, z), new Vector2(size.x + thickness * 2f, thickness), PaleGold);
            MakeLine(parent, new Vector3(center.x - hx, center.y, z), new Vector2(thickness, size.y + thickness * 2f), PaleGold);
            MakeLine(parent, new Vector3(center.x + hx, center.y, z), new Vector2(thickness, size.y + thickness * 2f), PaleGold);
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.AddGoldBorder] {ex.Message}");
        }
    }

    // =====================================================================
    //  字体
    // =====================================================================

    /// <summary>给一个 TMP 装上简中字体（NotoSansSC）。字体拿不到时退回原来那套，至少不会更糟。</summary>
    private static void ApplyCjkFont(TMP_Text? tmp)
    {
        if (tmp == null) return;
        try
        {
            var font = Light.UI.Window.MenuTextTemplate2.Font;
            if (font != null)
            {
                tmp.font = font;
                var mat = Light.UI.Window.MenuTextTemplate2.FontMaterial;
                if (mat != null) tmp.fontSharedMaterial = mat;
                return;
            }

            Light.UI.HudUI.HudUIFont.EnsureLoaded();
            var fb = Light.UI.HudUI.HudUIFont.FontAsset;
            if (fb != null)
            {
                tmp.font = fb;
                var fbm = Light.UI.HudUI.HudUIFont.FontMaterial;
                if (fbm != null) tmp.fontSharedMaterial = fbm;
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.ApplyCjkFont] {ex.Message}");
        }
    }

    /// <summary>
    /// 把窗口里**所有** TMP 统一刷成简中字体（含 GradientButton 内部那条文字）。
    ///
    /// 为什么要"再刷一遍"：<see cref="GradientButton.Create"/> 建文字时如果
    /// <c>MenuTextTemplate2.Font</c> 还没解析出来，它会退回 <c>HudUIFont</c>（可能是 Barlow，
    /// **没有中文字形**）→ 中文变方块。这里是最后一道保险，且调用点都在"界面已经建完"之后。
    /// </summary>
    private void ApplyCjkFontToAll()
    {
        try
        {
            if (_windowObj == null) return;

            var font = Light.UI.Window.MenuTextTemplate2.Font;
            if (font == null)
            {
                LightLogger.LogWarning("[MusicPlayerWindow] 简中字体不可用，中文可能显示为方块（见 MenuTextTemplate2 日志）");
                return;
            }
            var mat = Light.UI.Window.MenuTextTemplate2.FontMaterial;

            int n = 0;
            foreach (var tmp in _windowObj.GetComponentsInChildren<TextMeshPro>(true))
            {
                if (tmp == null) continue;
                tmp.font = font;
                if (mat != null) tmp.fontSharedMaterial = mat;
                n++;
            }

            LightLogger.Log($"[MusicPlayerWindow] 已把 {n} 段文字刷成简中字体 {font.name}");
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.ApplyCjkFontToAll] {ex.Message}");
        }
    }

    // ⚠️ 原来这里有一整套 ResolveGlyphs() / PickGlyph() / PlayCandidates / PauseCandidates ——
    //    "按字体实际有的字形挑 ▶ / ❚❚，都没有就退到 ASCII"。
    //    2026-10-05 用户要求按钮文字一律用汉字，字形探测**不再需要**，已整段删除
    //    （见 PlayLabel / PauseLabel / NextLabel 上的注释）。

    // =====================================================================
    //  按钮回调（每个都只做一件事，异常绝不外逃）
    // =====================================================================

    private void OnOpenFolder()
    {
        try { MusicLibrary.OpenFolder(); }
        catch (Exception ex) { LightLogger.LogError("[MusicPlayerWindow.OnOpenFolder]", ex); }
    }

    private void OnRefreshList()
    {
        try
        {
            EnsurePlayer();
            _player?.ReloadLibrary();

            RefreshList();
            RefreshDynamic();

            int count = _player != null ? _player.Library.Count : 0;
            LightLogger.Log($"[MusicPlayerWindow] 已刷新列表：{count} 首");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.OnRefreshList]", ex);
        }
    }

    private void OnPrevPage()
    {
        try
        {
            EnsurePlayer();
            // ⚠️ PrevPage/NextPage 只动分页、**不动音频**（正在放的歌不会被翻页打断）；
            //    越界由 MusicLibrary 内部夹紧，这里不用再判。
            bool changed = _player != null && _player.PrevPage();
            if (changed) RefreshList();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.OnPrevPage]", ex);
        }
    }

    private void OnNextPage()
    {
        try
        {
            EnsurePlayer();
            bool changed = _player != null && _player.NextPage();
            if (changed) RefreshList();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.OnNextPage]", ex);
        }
    }

    /// <summary>暂停按钮：正在播 → 暂停；否则什么都不做（避免"没在播也点暂停"的怪状态）。</summary>
    private void OnPauseClick()
    {
        try
        {
            EnsurePlayer();
            if (_player == null) return;
            if (_player.IsPlaying) _player.Pause();
            RefreshDynamic();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.OnPauseClick]", ex);
        }
    }

    /// <summary>播放按钮：暂停中 → 继续；没在播 → 从当前曲目（没有就第一首）开始。</summary>
    private void OnPlayClick()
    {
        try
        {
            EnsurePlayer();
            if (_player == null) return;

            if (_player.IsPaused) _player.Resume();
            else if (!_player.IsPlaying) _player.TogglePlayPause();   // 内部会挑当前/第一首并处理空列表
            RefreshDynamic();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.OnPlayClick]", ex);
        }
    }

    /// <summary>
    /// 「下一首」按钮：切到下一首。
    ///
    /// ⚠️ 用 <see cref="MusicPlayer.Next"/>（= 内部 <c>Step(+1)</c>）而不是自己算下标：
    ///    它已经处理了"空列表 / 当前没在播 / 循环模式（单曲循环 / 随机）/ 0.6 秒防连点冷却"。
    ///    写法与其它 handler 一致：<c>EnsurePlayer()</c> → <c>_player?.X()</c> → <c>RefreshDynamic()</c>。
    /// </summary>
    private void OnNextClick()
    {
        try
        {
            EnsurePlayer();
            _player?.Next();
            RefreshDynamic();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.OnNextClick]", ex);
        }
    }

    private void OnLoopModeClick()    {
        try
        {
            EnsurePlayer();
            _player?.CycleLoopMode();     // 列表循环 → 单曲循环 → 随机播放 → …
            RefreshDynamic();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.OnLoopModeClick]", ex);
        }
    }

    /// <summary>点列表行 → 播这一首。</summary>
    private void OnRowClicked(int row)
    {
        try
        {
            if (row < 0 || row >= _rowTracks.Count) return;
            int idx = _rowTracks[row];
            if (idx < 0) return;

            EnsurePlayer();
            _player?.PlayAt(idx, true);
            RefreshDynamic();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.OnRowClicked]", ex);
        }
    }

    private void EnsurePlayer()
    {
        try
        {
            if (_player == null) _player = MusicPlayer.Instance;
        }
        catch { }
    }

    // =====================================================================
    //  音量控件（读数 / ± 步进 / 拖动）
    // =====================================================================

    /// <summary>「-」按钮：音量 −5%。</summary>
    private void OnVolumeDown() => StepVolume(-VolumeStep);

    /// <summary>「+」按钮：音量 +5%。</summary>
    private void OnVolumeUp() => StepVolume(VolumeStep);

    /// <summary>点一次 ± ：按步进改音量（夹在 0~1），改完**立刻重画**，不等下一帧。</summary>
    private void StepVolume(float delta)
    {
        try
        {
            EnsurePlayer();
            if (_player == null) return;

            float v = Mathf.Clamp01(_player.Volume + delta);
            _player.SetVolume(v);
            RefreshVolumeVisual(v);

            // 点按钮是离散动作（不像拖动那样每帧都来），这里打一行日志是合适的
            LightLogger.Log($"[MusicPlayerWindow] 音量 {(delta >= 0f ? "+" : "-")}{Mathf.Abs(VolumeStep) * 100f:F0}%" +
                            $" → {FormatVolume(v)}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.StepVolume]", ex);
        }
    }

    /// <summary>
    /// 每帧轮询音量条拖动（本工程既有做法：<c>GradientButton.PollRightClick</c> +
    /// <c>BackgroundPanel.TickInput</c>，都是"每帧读 <c>Input</c> + 命中判定"）。
    ///
    /// 状态机：
    ///   ① 左键**按下**那一帧：<c>OverlapPoint</c> 命中抓取区 → 进入拖动（并"点哪跳哪"，同原版滑条）
    ///   ② 按住期间：每帧按鼠标的窗口本地 x 重算音量（连续、无步进）
    ///   ③ 松手：退出拖动，并**只在这时**打一条日志
    ///      —— 拖动过程每帧都调 SetVolume，途中打日志会把 LightLog 刷爆。
    ///
    /// 没在拖动时这个函数几乎不干活（只在鼠标按下那一帧才算相机/坐标），
    /// 所以挂在 Update 里每帧调用是便宜的。
    ///
    /// ⚠️ 异常绝不外逃（Update 里抛异常会中断 MonoBehaviour 消息链），所以整个方法体自己 try/catch。
    /// </summary>
    private void TickVolumeDrag()
    {
        try
        {
            if (_volBarHit == null) return;

            // ① 按下
            if (Input.GetMouseButtonDown(0))
            {
                _volDragging = false;

                // ⚠️ 依次试**两台**候选相机（"画 UI 层的相机" 与 Camera.main）：
                //    正交相机的 ScreenToWorldPoint 依赖各自的 orthographicSize，
                //    万一我们挑错相机（某场景两台都画 UI 层、但视口/尺寸不同），
                //    只试一台就会表现为"音量条完全点不动"。谁能命中抓取区就用谁。
                foreach (var cam in PointerCameraCandidates())
                {
                    if (cam == null) continue;
                    if (!TryGetWorldOnPlane(cam, out var w)) continue;
                    if (!_volBarHit.OverlapPoint(w)) continue;

                    _volDragCam = cam;              // 拖动期间**锁定**这台，避免中途换相机让数值跳
                    _volDragging = true;
                    if (TryGetLocal(cam, out var start)) ApplyVolumeFromLocalX(start.x);
                    break;
                }
            }

            // ② 按住（拖）
            if (_volDragging && Input.GetMouseButton(0) && _volDragCam != null)
            {
                if (TryGetLocal(_volDragCam, out var local)) ApplyVolumeFromLocalX(local.x);
            }

            // ③ 松手
            if (_volDragging && !Input.GetMouseButton(0))
            {
                _volDragging = false;
                _volDragCam = null;
                EnsurePlayer();
                LightLogger.Log($"[MusicPlayerWindow] 音量拖动结束：" +
                                $"{FormatVolume(_player != null ? _player.Volume : 0f)}");
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.TickVolumeDrag] {ex.Message}");
        }
    }

    /// <summary>把**窗口本地坐标的 x** 映射成 0~1 音量并落到播放核心（拖动唯一的值来源）。</summary>
    private void ApplyVolumeFromLocalX(float localX)
    {
        try
        {
            // 映射范围与"手柄绘制"严格用同一套（左右各缩进半个手柄宽）：
            // 这样鼠标压在哪儿、手柄就跟到哪儿；两端也不会出现"手柄还没贴到边就已经 100%"。
            float left = LeftCenterX - VolumeBarWidth * 0.5f;
            float travel = VolumeBarWidth - VolumeKnobWidth;
            float v = Mathf.Clamp01((localX - (left + VolumeKnobWidth * 0.5f)) / travel);

            EnsurePlayer();
            if (_player == null) return;

            _player.SetVolume(v);
            RefreshVolumeVisual(v);      // 拖动中当帧就画（RefreshDynamic 同帧稍后还会再兜一次）
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.ApplyVolumeFromLocalX] {ex.Message}");
        }
    }

    /// <summary>
    /// 把音量（0~1）**画出来**：读数文字 + 填充条宽度 + 手柄位置。
    /// 拖动时每帧都会被调用，所以文字只在"真的变了"的时候写（少触发 TMP 重排）。
    /// </summary>
    private void RefreshVolumeVisual(float volume)
    {
        try
        {
            volume = Mathf.Clamp01(volume);

            string txt = FormatVolume(volume);
            if (_volumeText != null && _volumeText.text != txt) _volumeText.text = txt;

            // 手柄的可移动范围要比槽**左右各缩进半个手柄宽**，否则拖到两端手柄会戳出槽外。
            // 于是"手柄中心"就是视觉上的当前音量位置，填充条画到它为止。
            float left = LeftCenterX - VolumeBarWidth * 0.5f;
            float knobX = left + VolumeKnobWidth * 0.5f + (VolumeBarWidth - VolumeKnobWidth) * volume;

            if (_volKnob != null)
                _volKnob.transform.localPosition = new Vector3(knobX, VolumeBarY, 0.02f);

            if (_volBarFill != null)
            {
                float w = knobX - left;
                _volBarFill.enabled = w > 0.005f;        // 音量 0 时整条隐藏（否则会留一个 0 宽的小疙瘩）
                if (!_volBarFill.enabled) return;

                _volBarFill.size = new Vector2(w, VolumeFillHeight);
                _volBarFill.transform.localPosition = new Vector3(left + w * 0.5f, VolumeBarY, 0.04f);
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.RefreshVolumeVisual] {ex.Message}");
        }
    }

    /// <summary>音量读数文本（读数与日志共用一处拼串，免得两处式子写不一样）。</summary>
    private static string FormatVolume(float volume)
        => $"音量 {Mathf.RoundToInt(Mathf.Clamp01(volume) * 100f)}%";

    /// <summary>
    /// 把鼠标位置换算到"窗口所在的那张平面"上的世界坐标。
    ///
    /// ⚠️ 相机**不能**想当然用 <c>Camera.main</c>：本窗口在 layer 5，大厅/主菜单里画 layer 5 的往往是
    ///    UI Camera（Main Camera 的 cullingMask 里没有 5，见 AGENTS.md §4.3）。
    ///    用错相机 → 世界坐标整体偏移 → 命中判定永远失败（表现为"音量条拖不动"）。
    ///    → 见 <see cref="PointerCameraCandidates"/>：按下时两台都试。
    ///
    /// ⚠️ 透视相机下 <c>ScreenToWorldPoint</c> 的 x/y 取决于传入的 z（屏深）→ 取"窗口所在平面"的深度；
    ///    正交相机这个 z 不影响 x/y。统一这么算，两种相机都对。
    /// </summary>
    private bool TryGetWorldOnPlane(Camera? cam, out Vector3 world)
    {
        world = Vector3.zero;

        try
        {
            if (cam == null || _windowObj == null) return false;

            var mp = Input.mousePosition;

            float depth = cam.WorldToScreenPoint(_windowObj.transform.position).z;
            if (depth <= 0.01f) depth = mp.z;      // 兜底：物体在相机背后时别用那个负值

            world = cam.ScreenToWorldPoint(new Vector3(mp.x, mp.y, depth));
            return true;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.TryGetWorldOnPlane] {ex.Message}");
            return false;
        }
    }

    /// <summary>鼠标位置 → 窗口本地坐标（先换成世界坐标，再用窗口内容根做 <c>InverseTransformPoint</c>）。</summary>
    private bool TryGetLocal(Camera? cam, out Vector2 local)
    {
        local = Vector2.zero;

        try
        {
            if (_screen == null) return false;
            if (!TryGetWorldOnPlane(cam, out var world)) return false;

            var lp = _screen.transform.InverseTransformPoint(world);
            local = new Vector2(lp.x, lp.y);
            return true;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.TryGetLocal] {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 候选指针相机（最多两台，按"更可能是画 UI 的那台"排序）：
    ///   ① <see cref="ResolveUiCamera"/> —— 画 UI 层(5)、depth 最大的那台；
    ///   ② <c>Camera.main</c> —— 它和①不是同一台时才加进来（BackgroundPanel 一直用它做命中，是个有效兜底）。
    /// </summary>
    private static Camera?[] PointerCameraCandidates()
    {
        try
        {
            var ui = ResolveUiCamera();
            var main = Camera.main;

            if (ui == null) return new Camera?[] { main };
            if (main == null || main == ui) return new Camera?[] { ui };   // Unity 的 == 能识别假 null
            return new Camera?[] { ui, main };
        }
        catch { return new Camera?[] { null }; }
    }

    /// <summary>找"画 UI 层(5)的那台相机"（多台都画就取 depth 最大的那台；一台都没有才退回 Camera.main）。</summary>
    private static Camera? ResolveUiCamera()
    {
        try
        {
            int ui = LayerExpansion.GetUILayer();
            Camera? best = null;

            foreach (var cam in Camera.allCameras)
            {
                if (cam == null) continue;
                if ((cam.cullingMask & (1 << ui)) == 0) continue;   // 不画 UI 层的相机算不出 UI 的屏幕坐标
                if (best == null || cam.depth > best.depth) best = cam;
            }

            return best != null ? best : Camera.main;
        }
        catch { return Camera.main; }
    }

    // =====================================================================
    //  追加需求：播放器在播 + 玩家在主界面 → 压住原版主界面 BGM（可还原）
    // =====================================================================

    /// <summary>
    /// **原版主界面 BGM 的压制/还原**（用户追加需求："如果播放器有音频在播放，且玩家在主界面，
    /// 那么停掉主界面的背景音"，且"必须能恢复"）。
    ///
    /// 判定条件（唯一真源，每帧/低频重判，**不记录"曾经发生过什么"**）：
    ///     压制 = <c>MusicPlayer.IsPlaying</c> && 当前场景是 <c>MainMenu</c>
    /// → 于是"暂停 / 放完 / 出错停下 / 切走场景"这些恢复时机全都自然覆盖到了，
    ///   不需要在 MusicPlayer 里到处埋回调（那种写法最容易漏一条路径）。
    ///
    /// ⚠️ 为什么**没有**照抄 <c>BackgroundRenderer.MuteVanillaMusic()</c>（我先读了它）：
    ///    它是走 <c>SoundManager.SetChannelVolume(0f, ref unused, "MusicVolume")</c>
    ///    —— 改的是 AudioMixer 里 "MusicVolume" 这个**全局通道**参数，还原靠
    ///    <c>SoundManager.UpdateChannelVolumes()</c>（按设置重算整条通道）。
    ///    两条问题：① 它是"设置里该是多少就多少"，还原不了"当时运行期的实际状态"；
    ///    ② **会和视频那条线打架** —— BackgroundRenderer 在"视频有音轨"时也压这条通道，
    ///       我们这边一还原就会把通道恢复，视频播放时 BGM 又冒出来（反之亦然）。
    ///    所以这里改用**对象级**做法：直接压 <c>Ambience/MainMenuBgMusic</c> 上那个 AudioSource
    ///    的 <c>mute</c>，还原时按"压之前记下的 volume/mute"原样写回。
    ///    这既满足用户"记住原来的状态再写回"的要求，也完全不碰全局通道（视频那条线不受影响）。
    ///    ※ 找物体的写法与 BackgroundRenderer.HideAmbienceDecor 一致：主界面里那个场景根物体就叫 Ambience。
    ///
    /// ⚠️ <paramref name="force"/>：绕过低频节流（关窗时用，让"关窗"这个时刻立刻生效）。
    /// </summary>
    private void TickMainMenuBgm(bool force = false)
    {
        try
        {
            if (!force && ++_bgmTick < BgmSyncEvery) return;
            _bgmTick = 0;

            EnsurePlayer();
            bool wantMute = _player != null && _player.IsPlaying && SafeSceneName() == "MainMenu";

            if (wantMute == _bgmMuted)
            {
                // 状态没变 = 稳态。这里**顺手重申一次** mute（同本工程"每帧保图守卫"的思路）：
                // 万一有别的流程把它改回来，下一个同步周期就补上 ——
                // 绝不会出现"原版 BGM 和我们的歌一起响"这种最难看的结果。
                if (_bgmMuted && _menuBgm != null) _menuBgm.mute = true;
                return;
            }

            if (wantMute) MuteMainMenuBgm();
            else RestoreMainMenuBgm();
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.TickMainMenuBgm] {ex.Message}");
        }
    }

    /// <summary>压住主界面 BGM：**先记状态再改**，且只改 <c>mute</c>（volume 原样留着）。</summary>
    private void MuteMainMenuBgm()
    {
        try
        {
            var src = FindMainMenuBgm();
            if (src == null)
            {
                // 找不到就先不做（**不置 _bgmMuted**，下次同步还会再试）。
                // 只在第一次记一行，免得每 10 帧刷一条。
                if (!_bgmLoggedMissing)
                {
                    _bgmLoggedMissing = true;
                    LightLogger.LogWarning("[MusicPlayerWindow] 主界面里没找到 Ambience/MainMenuBgMusic 的音源，" +
                                           "本次不压原版 BGM（之后每次同步还会再试）");
                }
                return;
            }

            _menuBgm = src;
            _bgmSavedVolume = src.volume;
            _bgmSavedMute = src.mute;
            _bgmSavedPlaying = src.isPlaying;      // 只用于日志/诊断，**还原时绝不拿它去 Play**

            src.mute = true;
            _bgmMuted = true;

            LightLogger.Log($"[MusicPlayerWindow] 已压住原版主界面 BGM（{SafeName(src.gameObject.name)}）：" +
                            $"原 volume={_bgmSavedVolume:F2} mute={_bgmSavedMute} isPlaying={_bgmSavedPlaying}" +
                            $"（只改 mute，不碰 volume / 不主动播放）");
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.MuteMainMenuBgm] {ex.Message}");
        }
    }

    /// <summary>
    /// 还原主界面 BGM：把压之前记下的 <c>volume</c> / <c>mute</c> 原样写回。
    ///
    /// ⚠️⚠️ **绝不调用 Play / Pause / Stop**：原版那个音源有可能本来就没在播
    ///    （比如场景刚加载还没起播、或者它播完了一轮）。一旦我们主动 <c>Play()</c>，
    ///    就变成用户明令禁止的"把本来没播的音乐给放起来"。
    ///    我们只改过 <c>mute</c>，所以写回 <c>mute</c> 就是完整的还原。
    /// </summary>
    private void RestoreMainMenuBgm()
    {
        try
        {
            _bgmMuted = false;

            var src = _menuBgm;
            _menuBgm = null;

            // Unity 假 null 判定：切场景会把那个音源连根销毁 → 没什么可还原的
            //（新场景的实例是全新的，本来也不会带着我们的静音）
            if (src == null) return;

            src.volume = _bgmSavedVolume;
            src.mute = _bgmSavedMute;

            LightLogger.Log($"[MusicPlayerWindow] 已还原原版主界面 BGM：volume={_bgmSavedVolume:F2} " +
                            $"mute={_bgmSavedMute}（压它的时候在播={_bgmSavedPlaying}，已恢复原样）");
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.RestoreMainMenuBgm] {ex.Message}");
        }
    }

    /// <summary>
    /// 找原版主界面 BGM 的 <see cref="AudioSource"/>。
    ///
    /// 与 <c>BackgroundRenderer.HideAmbienceDecor</c> 同一套定位方式：主界面里那个场景根物体就叫
    /// <c>Ambience</c>（本工程日志实测它的子物体是 starfield / MainMenuBgMusic / PlayerParticles）。
    /// 匹配顺序（从准确到宽松，避免误伤 starfield 之类的音效）：
    ///   ① 子物体名字**正好**是 <c>MainMenuBgMusic</c>；
    ///   ② 名字里带 <c>music</c>（大小写无关）；
    ///   ③ 兜底：Ambience 下**唯一**"在播且 loop"的音源。
    /// 都不满足就返回 null —— 调用方会保持原样（宁可不静音，也不要静错东西）。
    /// </summary>
    private static AudioSource? FindMainMenuBgm()
    {
        try
        {
            var ambience = GameObject.Find("Ambience");
            if (ambience == null) return null;

            var all = ambience.GetComponentsInChildren<AudioSource>(true);
            if (all == null || all.Length == 0) return null;

            foreach (var s in all)
                if (s != null && s.gameObject.name == "MainMenuBgMusic") return s;

            foreach (var s in all)
            {
                if (s == null) continue;
                if (s.gameObject.name.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0) return s;
            }

            AudioSource? only = null;
            int loops = 0;
            foreach (var s in all)
            {
                if (s == null || !s.isPlaying || !s.loop) continue;
                only = s;
                loops++;
            }
            return loops == 1 ? only : null;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.FindMainMenuBgm] {ex.Message}");
            return null;
        }
    }

    // =====================================================================
    //  刷新
    // =====================================================================

    /// <summary>重建列表内容（开窗 / 翻页 / 刷新列表时调用）。空列表不崩：全部行隐藏 + 显示提示。</summary>
    private void RefreshList()
    {
        try
        {
            EnsurePlayer();
            var lib = _player != null ? _player.Library : null;

            int shown = 0;
            IReadOnlyList<MusicTrack> page = lib != null ? lib.GetPage(lib.CurrentPage) : new List<MusicTrack>();

            for (int i = 0; i < _rowButtons.Count; i++)
            {
                var btn = _rowButtons[i];
                if (btn == null) continue;

                bool has = lib != null && i < page.Count;
                btn.SetActive(has);

                if (!has)
                {
                    if (i < _rowTracks.Count) _rowTracks[i] = -1;
                    continue;
                }

                var track = page[i];
                int global = lib!.IndexOf(track);
                if (i < _rowTracks.Count) _rowTracks[i] = global;

                // 序号用全局下标（翻页后也知道这是全库第几首）；名字按"全角宽度预算"截断，
                // 防止超长文件名溢出行外（字号已经开了自动缩放，双保险）。
                btn.SetText($"{global + 1:D2}. {Shorten(track.DisplayName, 22f)}");
                shown++;
            }

            if (_emptyText != null) _emptyText.gameObject.SetActive(shown == 0);

            RefreshRowVisuals();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayerWindow.RefreshList]", ex);
        }
    }

    /// <summary>每帧刷新：页码 / 曲库统计 / 模式文字 / 播放暂停高亮 / 状态行 / 行高亮。</summary>
    private void RefreshDynamic()
    {
        try
        {
            EnsurePlayer();
            if (_player == null)
            {
                if (_statusText != null) _statusText.text = "音频系统未就绪";
                return;
            }

            var lib = _player.Library;

            // ---- 页码 / 曲库统计 ----
            string page = $"第 {lib.CurrentPageDisplay} / {lib.TotalPages} 页";
            if (_pageText != null && _pageText.text != page) _pageText.text = page;

            string libInfo = $"曲库：{lib.Count} 首（{lib.TotalPages} 页）";
            if (_libText != null && _libText.text != libInfo) _libText.text = libInfo;

            // ---- 循环模式按钮：文字就是那四个字本身 ----
            string mode = MusicPlayer.LoopModeLabel(_player.LoopMode);
            if (_modeButton != null && _modeButton.Text != null && _modeButton.Text.text != mode)
                _modeButton.SetText(mode);

            // ---- 音量读数 / 音量条 ----
            // 拖动与 ± 都已经各自即时刷过，这里每帧再兜一次：万一别的地方（或重启后重建窗口）
            // 把音量改了，界面也会自己跟上 —— 界面永远以 MusicPlayer.Volume 为准。
            RefreshVolumeVisual(_player.Volume);

            // ---- 播放 / 暂停的选中态（正在播 → 「播放」亮；暂停中 → 「暂停」亮）----
            //      ⚠️ 只是按钮底色高亮，文字本身恒为「播放」/「暂停」，不做符号切换。
            bool playing = _player.IsPlaying;

        // ⚠️ 每帧同步"背景音共存"状态：播放/暂停/停止都会改变它。
        //    BackgroundRenderer.ExternalMute 的 setter 内部有"值没变就直接 return"，
        //    所以每帧调零开销。
        ApplyCoexist();
            bool paused = _player.IsPaused;
            _playButton?.SetSelected(playing || _player.IsLoading);
            _pauseButton?.SetSelected(paused);

            // ---- 状态行 ----
            var (status, color) = BuildStatus(playing, paused, lib.Count);
            if (_statusText != null)
            {
                if (_statusText.text != status) _statusText.text = status;
                _statusText.color = color;
            }

            // ---- 行高亮（正在播的那一行）----
            RefreshRowVisuals();
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.RefreshDynamic] {ex.Message}");
        }
    }

    /// <summary>
    /// 右上角状态行的**文字 + 颜色**。
    ///
    /// 2026-10-05 追加（用户）：在播 / 暂停时带**时间进度** —— `正在播放{曲名}  1:00 / 2:30`；
    /// 没在播就是原来的 `未在播放`。数据来自 <see cref="MusicPlayer.Position"/> /
    /// <see cref="MusicPlayer.Duration"/>（本次为此在 MusicPlayer 里新增的两个只读属性）。
    ///
    /// ⚠️ 曲名按"全角宽度预算"截断，而且预算**特意留小**（<see cref="StatusNameBudget"/>）：
    ///    时间那一段是"必须显示完整"的，所以名字长的时候优先牺牲名字。
    ///    外面每帧调一次（<see cref="RefreshDynamic"/>），但只有字符串真的变了才写 TMP
    ///    → 时间每秒跳一次，实际重排也就每秒一次。
    /// </summary>
    private (string, Color) BuildStatus(bool playing, bool paused, int count)
    {
        try
        {
            var p = _player;
            if (p == null) return ("音频系统未就绪", TextDim);

            if (p.IsLoading) return ($"加载中… {Shorten(p.NowPlayingName, 10f)}", PaleGold);
            if (!string.IsNullOrEmpty(p.LastError)) return ($"加载失败：{Shorten(p.LastError, 16f)}", TextWarn);
            if (p.IsMuted) return ("已静音", TextDim);

            // 时间进度：AudioSource.time / clip.length（对 stream:false 的 clip 同样有效）
            string time = $"{FormatTime(p.Position)} / {FormatTime(p.Duration)}";

            if (paused) return ($"已暂停 {Shorten(p.NowPlayingName, StatusNameBudget)}  {time}", TextDim);
            if (playing) return ($"正在播放{Shorten(p.NowPlayingName, StatusNameBudget)}  {time}", PaleGoldDim);

            if (count == 0) return ("Music 文件夹里还没有歌曲", TextDim);
            return ("未在播放", TextDim);    // ← 用户要的"没在播就显示 未在播放"
        }
        catch
        {
            return ("未在播放", TextDim);
        }
    }

    /// <summary>
    /// 状态行里"曲名"能占的**全角宽度预算**。
    /// 整行 = `正在播放`(4 个全角) + 曲名 + 两个空格 + `1:00 / 2:30`(约 5.5~7 个全角当量)，
    /// 右上角那个文字框只有 5.4 宽 → 曲名给 10 已经是"刚好卡住"的量，
    /// 再多就会把时间那一段挤掉（时间不能省）。
    /// </summary>
    private const float StatusNameBudget = 10f;

    /// <summary>
    /// 秒 → `m:ss`（`1:00` / `2:30` / `12:05`）。**超过 1 小时就写成 `61:30`**（用户明确说不用做小时位）。
    /// </summary>
    private static string FormatTime(float seconds)
    {
        try
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f) seconds = 0f;
            int total = Mathf.FloorToInt(seconds);
            return $"{total / 60}:{total % 60:D2}";
        }
        catch { return "0:00"; }
    }

    /// <summary>正在播的那一行高亮（<see cref="GradientButton.SetSelected"/> 会同时改底色和字色）。</summary>
    private void RefreshRowVisuals()
    {
        try
        {
            int cur = -1;
            if (_player != null)
            {
                var track = _player.CurrentTrack;
                if (track != null) cur = _player.Library.IndexOf(track);
            }

            for (int i = 0; i < _rowButtons.Count; i++)
            {
                var btn = _rowButtons[i];
                if (btn == null) continue;

                int idx = i < _rowTracks.Count ? _rowTracks[i] : -1;
                btn.SetSelected(cur >= 0 && idx == cur);
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayerWindow.RefreshRowVisuals] {ex.Message}");
        }
    }

    // =====================================================================
    //  小工具
    // =====================================================================

    /// <summary>
    /// 按"全角宽度预算"截断字符串（CJK/全角算 1，拉丁/数字算 0.55）。
    /// 行文字是左对齐且可能很长的文件名，截断 + 字体自动缩放双保险，保证不溢出行。
    /// </summary>
    private static string Shorten(string? s, float budget)
    {
        try
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            float w = 0f;
            int i = 0;
            for (; i < s!.Length; i++)
            {
                w += CharWeight(s[i]);
                if (w > budget) break;
            }

            if (i >= s.Length) return s;
            return s.Substring(0, Mathf.Max(1, i - 1)) + "…";
        }
        catch { return s != null ? s : string.Empty; }
    }

    private static float CharWeight(char c)
    {
        bool wide =
            (c >= 0x1100 && c <= 0x115F) ||      // 韩文字母
            (c >= 0x2E80 && c <= 0xA4CF) ||      // CJK 部首 ~ 注音/汉字
            (c >= 0xAC00 && c <= 0xD7A3) ||      // 韩文音节
            (c >= 0xF900 && c <= 0xFAFF) ||      // CJK 兼容汉字
            (c >= 0xFE30 && c <= 0xFE6F) ||      // CJK 兼容标点
            (c >= 0xFF00 && c <= 0xFF60) ||      // 全角字符
            (c >= 0xFFE0 && c <= 0xFFE6);
        return wide ? 1f : 0.55f;
    }

    private static string SafeName(string? s)
        => string.IsNullOrEmpty(s) ? "?" : s;

    private static void PlaySound(string clipName, float volume)
    {
        try
        {
            var clip = Light.UI.Window.VanillaAsset.FindSoundClip(clipName);
            if (clip != null) SoundManager.Instance?.PlaySound(clip, false, volume);
        }
        catch { }
    }
}
