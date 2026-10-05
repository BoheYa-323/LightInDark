using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Light.Utilities;
using Light.Tools;
using Light.UI.MainMenu;
using LightInDark.Core;
using TMPro;
using UnityEngine;

namespace Light.Patches;

/// <summary>
/// 自定义启动加载页（Windows 11 安装界面风格）。
/// - 背景：淡金色径向渐变光斑，偏左上分布，缓慢漂移/晃动/旋转；每个光斑周期性
///   分裂出一个小光斑飘散淡出（Fluent Bloom 流动感）。
/// - LOGO：淡入后持续上下浮动。
/// - 进度：多个加载步骤在固定锚点依次完成；步骤文字（含本步进度 (NN%) 同行显示）；
///   完成项缩小 0.5 并下移、旧完成项让位；全部完成后快速收拢淡出。
/// - 提示：固定 8 句随机取一句，每 2 秒更换且不重复，切换时淡出/淡入。
/// - 结束：绿色"加载成功！" 淡入→停留→下移出屏；点击任意处进入游戏（平滑呼吸闪烁）。
/// LOGO 浮动 / 背景光斑 / 提示轮换由独立协程每帧驱动，不随主流程 yield 卡顿。
/// 进入游戏仍走原接口：sceneChanger.AllowFinishLoadingScene() + startedSceneLoad = true。
/// </summary>
[HarmonyPriority(Priority.HigherThanNormal)]
[HarmonyPatch(typeof(SplashManager), nameof(SplashManager.Update))]
public static class LoadPatch
{
    // ======================= 可调参数 =======================

    // 总体
    // ⚠️ 这里原来有个 `MinLoadTime = 6f`（"加载动画至少 6 秒"），已删除。
    //    它有两种用法，现在都不需要了：
    //      ① 摊给每段假进度：`StepDuration = w/(8+1.2) * MinLoadTime` —— 假进度条整个没了
    //      ② 等待循环里的硬性下限：`Time.time - startTime < MinLoadTime` —— 已去掉
    //    想恢复"至少显示 N 秒"的话：在 5.5 的等待循环里加回
    //    `|| Time.time - <开头的 startTime> < N` 即可。
    // 完成项间距按尺度区分：当前项是全尺寸大字，下移需更宽间距；
    // 完成项彼此都是缩小到 0.5 的小字，间距收紧避免空洞。
    private const float FirstRowGap = 0.9f;        // 当前项(全尺寸) → 第一完成项 的下移间距
    private const float RowGap = 0.32f;            // 完成项(缩小0.5) 彼此之间的下移间距
    private const float FadeInDuration = 0.25f;    // 当前进度项淡入时长
    private const float CompletedHold = 0.15f;     // 步骤完成后的停顿

    // 每一步自己从 0% 爬到 100% 的**显示时长**。
    // ⚠️ 这不是"假进度"：活儿在本步开头就已经真干完了，这个时间只是让
    //    "这一条走完了"看得见；真慢的步骤会把它用掉，不会再额外多等。
    private const float StepRampTime = 0.30f;
    private const float MoveDuration = 0.15f;      // 完成项下移/缩放时长（摞文字速度快些）
    private const float SuccessFadeIn = 0.3f;      // "加载成功！"淡入时长
    private const float SuccessHold = 0.8f;        // "加载成功！"停留时长
    private const float TipInterval = 2f;          // 提示文字更换间隔
    private const float TipFade = 0.25f;           // 提示文字淡出/淡入时长

    // LOGO
    private const float LogoFadeIn = 0.6f;         // LOGO 淡入时长
    private const float LogoFloatCycle = 2f;       // 浮动周期
    private const float LogoFloatAmp = 0.025f;     // 浮动幅度（±，轻微）

    // 点击提示呼吸（平滑正弦，周期内无跳变）
    private const float ClickGlowCycle = 1.5f;     // 闪烁周期

    // 背景光斑
    private const int BlobCount = 4;
    private const float BlobMinCycle = 20f;        // 漂移最短周期
    private const float BlobMaxCycle = 40f;        // 漂移最长周期
    private const float BlobAlpha = 0.14f;         // 光斑透明度（低）
    private const float SplitMinInterval = 5f;     // 分裂最短间隔
    private const float SplitMaxInterval = 9f;     // 分裂最长间隔
    private const float SplitDuration = 3f;        // 一次分裂（子光斑飘散淡出）时长

    // 颜色
    private static readonly Color BlobColorA = new(0.96f, 0.83f, 0.54f, 1f);   // #F5D48A
    private static readonly Color BlobColorB = new(1.00f, 0.91f, 0.69f, 1f);   // #FFE7B0
    private static readonly Color ProgressGray = new(0.72f, 0.72f, 0.74f, 1f); // 进度文字初始灰白
    private static readonly Color ProgressGreen = new(0.486f, 0.988f, 0f, 1f); // #7CFC00
    private static readonly Color FailRed = new(0.93f, 0.26f, 0.22f, 1f);      // 步骤失败时的红
    private static readonly Color ClickGold = new(0.96f, 0.83f, 0.54f, 1f);     // #F5D48A 点击提示

    // 布局
    private static readonly Vector3 LogoPos = new(0f, 0.5f, -5f);
    private static readonly Vector3 StepAnchor = new(0f, -0.55f, -10f);   // 步骤文字锚点（LOGO 正下方）
    private static readonly Vector3 TipPos = new(0f, -3.6f, -10f);
    private static readonly Vector3 VersionPos = new(4.5f, -3.2f, -10f);
    private static readonly Vector3 ClickPos = new(0f, -1.0f, -10f);       // 点击进入提示（往上移）
    private static readonly Vector3 NewVersionPos = new(0f, -1.75f, -10f); // 新版本金字（在点击提示下面）

    // ---- 失败红字堆（屏幕左下角）----
    //
    // ⚠️ 位置**不写死常量**，而是运行时用相机把视口 (0,0) 转成世界坐标算出来 ——
    //    不然换个分辨率/宽高比就跑到屏幕外或者浮在中间了（上一版 -4.55/-3.35 就是估的）。
    private const float ErrMargin = 0.18f;     // 离屏幕左/下边缘的留白
    private const float ErrLineH = 0.34f;      // 行高（往上摞的步进）
    private const float ErrWidth = 7.0f;       // 每条红字的矩形宽度（左对齐用）
    /// <summary>UI 文字所在的 z（所有 CreateText 都放这一层）。</summary>
    private const float UiPlaneZ = -10f;

    // ======================= 加载步骤（**每一条都是真活儿**） =======================
    //
    // ⚠️⚠️ 旧实现是 9 个写死的文字 + 按固定时长走的**假进度条**：
    //      `while (elapsed < stepDuration)` 里只把时间映射成百分比，
    //      再在最后 `Time.time - startTime < MinLoadTime` 硬等满 6 秒。
    //      也就是说进度条和"到底干了什么"完全无关，加载永远是 6 秒起步。
    //
    // 现在改成：**步骤就是一个工作队列**，百分比 = 真实完成的步骤数 / 总步骤数，
    // 全部干完就结束（不再有硬性下限）。
    //
    // 想加步骤就在别处调 <see cref="RegisterStep"/>（在 SplashManager.Update 第一次跑到之前注册即可，
    // 也就是 LightPlugin.Load 里），或者在 <see cref="BuildDefaultSteps"/> 里加。

    /// <summary>
    /// 三个阶段（用户明确要求）：
    ///   1. MOD 加载 —— 模组自己的初始化
    ///   2. Tool 检查与下载 —— 看 Tools 目录里有没有 LightUpdater.exe，没有就下载
    ///   3. 应用 Tool 功能 —— 启用工具（本轮先占位，内容下轮再定）
    /// </summary>
    public const string PhaseModLoad = "MOD 加载";
    public const string PhaseToolCheck = "Tool 检查与下载";
    public const string PhaseToolApply = "应用 Tool 功能";

    /// <summary>一条加载步骤。<see cref="Routine"/> 非空时走它（可以自己写进度文本）。</summary>
    private sealed class LoadStep
    {
        public string Phase = PhaseModLoad;
        public string Label = "";
        public Action? Work;
        public Func<IEnumerator>? Routine;
    }

    private static readonly List<LoadStep> RegisteredSteps = new();

    /// <summary>
    /// 往**阶段 1（MOD 加载）**末尾追加一条真实的加载步骤。
    ///
    /// <paramref name="work"/> 里抛异常不会中断加载，只会记一条日志并把该步标记为失败。
    /// </summary>
    public static void RegisterStep(string label, Action work)
        => RegisterStep(PhaseModLoad, label, work);

    /// <summary>指定阶段注册一条步骤。</summary>
    public static void RegisterStep(string phase, string label, Action work)
    {
        if (string.IsNullOrEmpty(label) || work == null) return;
        RegisteredSteps.Add(new LoadStep { Phase = phase, Label = label, Work = work });
    }

    /// <summary>
    /// 组装完整的步骤表（三个阶段的顺序就是显示顺序）。
    /// 内置步骤全部**幂等**：重复调用只是重扫目录 / 命中缓存。
    /// </summary>
    private static List<LoadStep> BuildSteps()
    {
        var steps = new List<LoadStep>
        {
            // ===== 阶段 1：MOD 加载 =====
            new() { Phase = PhaseModLoad, Label = "正在检查工具目录...",  Work = EnsureToolsDir },
            new() { Phase = PhaseModLoad, Label = "正在解压资源包...",    Work = EnsureMainBackgroundAssets },
            new() { Phase = PhaseModLoad, Label = "正在加载语言数据...",  Work = EnsureChineseFont },
            new() { Phase = PhaseModLoad, Label = "正在准备游戏环境...",  Work = EnsureUserDataDir },

            // ===== 阶段 2：Tool 检查与下载 =====
            new() { Phase = PhaseToolCheck, Label = "正在检查 LightUpdater.exe...", Work = null,
                    Routine = ToolCheckRoutine },
            new() { Phase = PhaseToolCheck, Label = "正在检查模组更新...", Work = null,
                    Routine = UpdateCheckRoutine },

            // ===== 阶段 3：应用 Tool 功能 =====
            // 用户说内容下轮再定，这里先放两条**真的会做点事**的占位：
            new() { Phase = PhaseToolApply, Label = "正在校验工具文件...", Work = VerifyUpdaterFile },
            new() { Phase = PhaseToolApply, Label = "正在应用工具配置...", Work = ApplyToolPlaceholder },
        };

        if (RegisteredSteps.Count > 0) steps.AddRange(RegisteredSteps);
        return steps;
    }

    // ======================= 阶段 2 / 3 的实现 =======================

    /// <summary>阶段标题：金色大字，插进"完成项"队列里跟着一起下移。</summary>
    private static IEnumerator EmitPhaseHeader(SplashManager instance, string phase)
    {
        var header = CreateText(instance, "LoadPhaseHeader", StepAnchor, FontStyles.Bold, 1.3f, TextAlignmentOptions.Center);
        header.text = $"── {phase} ──";
        header.color = new Color(ClickGold.r, ClickGold.g, ClickGold.b, 0f);

        yield return FadeAlpha(header, 0f, 1f, FadeInDuration);

        completedSteps.Add(header);
        AnimateCompletedStep(header);

        float moveElapsed = 0f;
        while (moveElapsed < MoveDuration)
        {
            moveElapsed += Time.deltaTime;
            CleanupTweens();
            yield return null;
        }
        CleanupTweens();

        // 标题后面的步骤要重新建一个锚点文本
        currentStepText = CreateText(instance, "LoadStepText", StepAnchor, FontStyles.Bold, 1f, TextAlignmentOptions.Center);
        currentStepText.text = "";
        currentStepText.color = new Color(0f, 0f, 0f, 0f);
    }

    /// <summary>阶段 2 第一步：Tools 目录里有没有 LightUpdater.exe，没有就下载。</summary>
    private static IEnumerator ToolCheckRoutine()
    {
        bool exists = LightToolManager.UpdaterExists;
        LightLogger.Log($"[Load] {LightToolManager.UpdaterExeName} " +
                        $"{(exists ? "已存在" : "不存在，准备下载")}：{LightToolManager.UpdaterPath}");

        // 检查本身是瞬时的，给个短爬升让这一步看得见
        float e = 0f;
        while (e < StepRampTime * 0.5f)
        {
            e += Time.deltaTime;
            float p = Mathf.Clamp01(e / (StepRampTime * 0.5f));
            currentStepText.text = $"正在检查 {LightToolManager.UpdaterExeName}... ({Mathf.RoundToInt(p * 100f)}%)";
            currentStepText.color = Color.Lerp(ProgressGray, ProgressGreen, p);
            yield return null;
        }

        if (exists)
        {
            routineFinalText = $"{LightToolManager.UpdaterExeName} 已就位";
            routineFinalColor = ProgressGreen;
            yield break;
        }

        // ---- 下载：这一行**自己写进度文本**：下载 {Name} 中...(10 kb/s 50%) ----
        bool done = false;
        bool ok = false;
        string failMsg = "";

        yield return LightToolManager.DownloadUpdater(
            onTick: (received, total, pct, kbPerSec) =>
            {
                string pctText = total > 0 ? $"{Mathf.RoundToInt(pct * 100f)}%" : "…";
                currentStepText.text =
                    $"下载 {LightToolManager.UpdaterExeName} 中...({kbPerSec:F0} kb/s {pctText})";
                currentStepText.color = Color.Lerp(ProgressGray, ProgressGreen, total > 0 ? pct : 0.5f);
            },
            onDone: (success, msg) =>
            {
                done = true;
                ok = success;
                failMsg = msg;
            });

        // 协程结束后 onDone 一定已经调过了
        _ = done;

        if (ok)
        {
            routineFinalText = $"下载 {LightToolManager.UpdaterExeName} 完成";
            routineFinalColor = ProgressGreen;
        }
        else
        {
            // ⚠️ 用户要求：下载失败就**显示一行红字，然后继续执行后面的步骤**。
            //    站点可能还在维护（实测现在就是 404 + 「维护中」），绝不能卡在加载页。
            //    用 routineFinalText 交给外层，否则会被 "{Label} (100%)" 覆盖掉。
            routineFinalText = $"下载 {LightToolManager.UpdaterExeName} 失败：{failMsg}";
            routineFinalColor = FailRed;
            LightLogger.LogWarning($"[Load] 下载失败但不阻断加载：{failMsg}");
            // 同时丢进左下角的红字堆（会一直留到点击之后）
            ReportError($"下载 {LightToolManager.UpdaterExeName} 失败：{failMsg}");
        }
    }

    /// <summary>阶段 2 第二步：拉远端 version.json 比对版本。</summary>
    private static IEnumerator UpdateCheckRoutine()
    {
        // ---- 配置：可以整个关掉自动检查 ----
        // 位置：设置菜单 → Light 页签 →「自动检查更新」，落盘在 Settings.json 的 AutoCheckUpdate
        bool autoCheck = true;
        try { autoCheck = LightPlugin.LightSettingsData?.AutoCheckUpdate ?? true; } catch { }

        if (!autoCheck)
        {
            LightLogger.Log("[Load] 自动检查更新已关闭（设置 → Light → 自动检查更新），跳过版本比对");
            routineFinalText = "已跳过更新检查（设置 → Light → 自动检查更新）";
            routineFinalColor = ProgressGray;
            newVersionFound = false;
            newVersionText = "";
            yield break;
        }

        var remote = new LightToolManager.RemoteVersion();
        float start = Time.realtimeSinceStartup;

        // 边等边爬进度（这一步没有细粒度进度可报，就用"等待中"的样式）
        // ⚠️ 不能把 yield 放进 try/catch，所以这里用 while + 标志位。
        bool finished = false;
        var routine = LightToolManager.FetchRemoteVersion(remote);
        while (true)
        {
            bool moved;
            try { moved = routine.MoveNext(); }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[Load] 版本检查异常：{ex.Message}");
                break;
            }
            if (!moved) break;

            float t = Time.realtimeSinceStartup - start;
            currentStepText.text = $"正在检查模组更新... ({Mathf.RoundToInt(Mathf.Clamp01(t / 1.5f) * 100f)}%)";
            currentStepText.color = ProgressGray;

            // 协程里 yield 的是 UnityWebRequestAsyncOperation，这里统一等一帧
            yield return null;
        }
        finished = true;
        _ = finished;

        // ---- 比对 ----
        newVersionFound = false;
        newVersionText = "";
        if (!remote.Fetched)
        {
            // ⚠️ 用户要求：version.json 没拿到**也要打红字**（和下载失败一样进左下角那一摞）
            LightLogger.Log($"[Load] 版本检查跳过：{remote.Error}");
            ReportError($"更新检查失败：{remote.Error}");
            yield break;
        }

        string curLight = SafeLocalVersion(() => LightPlugin.Version);
        string curApi = SafeLocalVersion(() => LightInDark.LIDPlugin.Version);

        bool lightOld = LightToolManager.IsOutdated(curLight, remote.LightVersion);
        bool apiOld = LightToolManager.IsOutdated(curApi, remote.ApiVersion);

        if (lightOld && apiOld)
        {
            newVersionFound = true;
            newVersionText = $"Light 和 Light（API） 有新版本 v{remote.LightVersion}。按Ctrl打开下载链接。";
        }
        else if (lightOld)
        {
            newVersionFound = true;
            newVersionText = $"Light 有新版本 v{remote.LightVersion}。按Ctrl打开下载链接。";
        }
        else if (apiOld)
        {
            newVersionFound = true;
            newVersionText = $"Light（API） 有新版本 v{remote.ApiVersion}。按Ctrl打开下载链接。";
        }

        LightLogger.Log($"[Load] 版本比对：本地 Light={curLight} LightInDark={curApi}｜" +
                        $"远端 Light={remote.LightVersion} LightInDark={remote.ApiVersion}｜" +
                        $"有新版本={newVersionFound}");
    }

    // =====================================================================
    //  加载失败统一入口
    // =====================================================================

    /// <summary>
    /// **加载失败统一入口。以后任何地方加载失败都调这个。**
    ///
    /// 效果（用户要求）：
    ///   ① 红字先出现在**当前步骤文字的位置**（也就是"打印"的地方）
    ///   ② 飘到**左下角**，到位后**加粗**
    ///   ③ 后续的红字**自动往上摞**（index 0 在最下面）
    ///   ④ 一直留到玩家点击之后 —— 淡出顺序里它是**倒数第二个**（最后一个是 LOGO）
    ///
    /// 已经接上的调用点：
    ///   - 工具下载失败（阶段 2）
    ///   - version.json 拉取/解析失败（阶段 2）
    ///   - **任何同步步骤抛异常**（外层循环统一兜底，见 CoLoadLight）
    ///
    /// 线程/时机：只应该在加载协程运行期间调（那时 currentInstance 才有值）。
    /// 不在加载期调也不会炸，只是降级成只写日志。
    /// </summary>
    public static void ReportError(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return;

            LightLogger.LogWarning($"[Load][错误] {text}");

            var inst = currentInstance;
            if (inst == null)
            {
                // 不在加载页里 —— 降级成只记日志，不影响调用方
                return;
            }

            var tmp = CreateText(inst, "LoadErrorText", StepAnchor,
                FontStyles.Normal, 0.78f, TextAlignmentOptions.BottomLeft);
            if (tmp == null) return;

            tmp.text = text;
            tmp.color = new Color(FailRed.r, FailRed.g, FailRed.b, 0f);
            tmp.alignment = TextAlignmentOptions.BottomLeft;


            // 屏幕左下角（运行时按相机算，不写死）
            TryGetScreenCorner(out var bl, out var tr);
            float left = bl.x + ErrMargin;
            float bottomEdge = bl.y + ErrMargin;

            // 矩形宽度**按屏幕自适应**：写死的话屏幕窄 / 文字长会折行溢出
            float width = Mathf.Max(ErrWidth, tr.x - left - ErrMargin);
            try
            {
                tmp.rectTransform.sizeDelta = new Vector2(width, ErrLineH);
                tmp.enableWordWrapping = false;    // 一行到底，不折行
            }
            catch { }

            int idx = errorTexts.Count;
            float bottom = bottomEdge + idx * ErrLineH;              // ← 往上摞
            var to = new Vector3(left + width * 0.5f, bottom + ErrLineH * 0.5f, UiPlaneZ);

            // 起点 = 当前步骤文字的位置（"打印"的地方）
            Vector3 from = currentStepText != null
                ? currentStepText.transform.localPosition
                : StepAnchor;

            errorTexts.Add(tmp);
            inst.StartCoroutine(CoFlyError(tmp, from, to).WrapToIl2Cpp());
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[Load.ReportError]", ex);
        }
    }

    /// <summary>
    /// 求**屏幕左下角**在 UI 平面(z=-10)上的世界坐标。
    ///
    /// 做法：把 UI 平面到相机的距离算出来，再用 <c>ViewportToWorldPoint</c> 把
    /// 视口 (0,0) / (1,1) 换算到那个平面上。返回 false 时给一组保守的兜底值。
    /// </summary>
    private static bool TryGetScreenCorner(out Vector2 bottomLeft, out Vector2 topRight)
    {
        bottomLeft = new Vector2(-6.0f, -3.6f);   // 兜底（按 16:9 / ortho 3.6 估的）
        topRight = new Vector2(6.0f, 3.6f);

        try
        {
            var cam = Camera.main;
            if (cam == null) return false;

            float dist = Mathf.Abs(UiPlaneZ - cam.transform.position.z);
            if (dist < 0.01f) dist = 10f;

            var a = cam.ViewportToWorldPoint(new Vector3(0f, 0f, dist));
            var b = cam.ViewportToWorldPoint(new Vector3(1f, 1f, dist));

            bottomLeft = new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
            topRight = new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

            // 只在第一次算的时候打一行，方便核对（算错了从日志就能看出来）
            if (!loggedScreenCorner)
            {
                loggedScreenCorner = true;
                LightLogger.Log($"[Load] 屏幕范围（UI平面 z={UiPlaneZ}）：" +
                                $"左下=({bottomLeft.x:F2},{bottomLeft.y:F2}) " +
                                $"右上=({topRight.x:F2},{topRight.y:F2}) " +
                                $"相机={cam.name} 正交={cam.orthographic} size={cam.orthographicSize:F2} " +
                                $"相机z={cam.transform.position.z:F2} dist={dist:F2}");
            }
            return true;
        }
        catch { return false; }
    }

    private static bool loggedScreenCorner;

    /// <summary>红字：淡入 → 飘到左下角 → 加粗 → 定住。</summary>
    private static IEnumerator CoFlyError(TextMeshPro tmp, Vector3 from, Vector3 to)
    {
        tmp.transform.localPosition = from;

        // 淡入
        float e = 0f;
        while (e < 0.3f)
        {
            e += Time.deltaTime;
            if (tmp == null) yield break;
            tmp.alpha = Mathf.Clamp01(e / 0.3f) * 0.95f;
            yield return null;
        }

        // 飘到左下角
        e = 0f;
        while (e < 0.5f)
        {
            e += Time.deltaTime;
            if (tmp == null) yield break;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(e / 0.5f));
            tmp.transform.localPosition = Vector3.Lerp(from, to, k);
            yield return null;
        }

        if (tmp == null) yield break;
        tmp.transform.localPosition = to;

        // ⚠️ 到位后**加粗**
        try { tmp.fontStyle = FontStyles.Bold; } catch { }
    }

    private static string SafeLocalVersion(Func<string> get)
    {
        try { return get() ?? ""; } catch { return ""; }
    }

    /// <summary>阶段 3 占位①：确认下载下来的文件确实是个可执行文件（PE 头 MZ）。</summary>
    private static void VerifyUpdaterFile()
    {
        try
        {
            string path = LightToolManager.UpdaterPath;
            if (!File.Exists(path))
            {
                LightLogger.Log($"[Load] {LightToolManager.UpdaterExeName} 不在，跳过校验");
                return;
            }

            using var fs = File.OpenRead(path);
            int b0 = fs.ReadByte(), b1 = fs.ReadByte();
            bool isPe = b0 == 'M' && b1 == 'Z';
            LightLogger.Log($"[Load] 工具文件校验：{new FileInfo(path).Length / 1024} KB，PE 头={(isPe ? "正常" : "异常")}");
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[Load] 工具文件校验失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 阶段 3 占位②。用户说"应用 Tool 功能"的内容下轮再定，
    /// 这里先只记一行日志，保证阶段结构已经在跑。
    /// </summary>
    private static void ApplyToolPlaceholder()
    {
        bool ready = LightToolManager.UpdaterExists;
        LightLogger.Log($"[Load] 应用 Tool 功能（占位）：工具{(ready ? "已就绪" : "未就绪")}，具体功能待定");
    }

    // ======================= 真实步骤的实现 =======================

    /// <summary>
    /// 工具目录：<c>&lt;游戏根目录&gt;\Light_Data\Tools</c>
    /// （和既有的 <c>Light_Data\MainBackGround</c> 同一层，属于"模组自带的数据"）。
    /// </summary>
    public static string ToolsDir =>
        Path.Combine(BepInEx.Paths.GameRootPath, "Light_Data", "Tools");

    /// <summary>没有就建一个。返回目录是否可用。</summary>
    private static void EnsureToolsDir()
    {
        try
        {
            string dir = ToolsDir;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                LightLogger.Log($"[Load] Tools 目录不存在，已创建：{dir}");
            }
            else
            {
                LightLogger.Log($"[Load] Tools 目录已存在：{dir}");
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[Load.EnsureToolsDir]", ex);
        }
    }

    /// <summary>确保用户数据目录存在（&lt;persistentDataPath&gt;\LightInDark）。</summary>
    private static void EnsureUserDataDir()
    {
        try
        {
            string dir = LightPlugin.LightUserDataPath;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[Load.EnsureUserDataDir]", ex);
        }
    }

    /// <summary>释放内嵌背景素材（幂等：已存在会跳过）。</summary>
    private static void EnsureMainBackgroundAssets()
    {
        try
        {
            int n = BackgroundStore.EnsureExtracted();
            LightLogger.Log($"[Load] 背景素材检查完成（新释放 {n} 个）");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[Load.EnsureMainBackgroundAssets]", ex);
        }
    }

    /// <summary>提前解析简中字体（MenuTextTemplate2 模板）。</summary>
    private static void EnsureChineseFont()
    {
        try
        {
            var f = Light.UI.Window.MenuTextTemplate2.Font;
            LightLogger.Log($"[Load] 简中字体 = {(f != null ? f.name : "未找到")}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[Load.EnsureChineseFont]", ex);
        }
    }

    private static readonly string[] TipTexts =
    [
        "提示：本模组与大多数模组不兼容",
        "提示：你可以在%userprofile%/AppData/LocalLow/Innersloth/Among Us/LightInDark更改部分模组配置",
        "提示：大厅中，按下Shift可以无视碰撞箱。按下左/右Ctrl时，可以关闭左/右引擎的火",
        "感谢您选择Light In Dark!",
        "不要相信T氏的话",
        "本MOD半数代码为AI生成",
        "月痕制作组向你致意",
        "祝你好运！",
    ];

    private const string LoadSuccessTextCN = "加载成功！";
    private const string ClickToEnterTextCN = "-- 点击任意处进入游戏 --";

    // ======================= 运行时状态 =======================

    private static SpriteRenderer? logo;
    private static readonly List<SpriteRenderer> blobs = new();
    private static readonly List<SpriteRenderer> subBlobs = new();   // 分裂子光斑（与 blobs 同索引）
    private static readonly List<BlobAnim> blobAnims = new();

    private static TextMeshPro? currentStepText;   // 当前步骤文字（含百分比同行）
    private static TextMeshPro? tipText;
    private static TextMeshPro? successText;
    private static TextMeshPro? clickText;
    private static TextMeshPro? versionText;
    private static readonly List<TextMeshPro> completedSteps = new();
    private static readonly List<Tweener> moveTweens = new();         // 让位/移出动画

    // 独立环境协程共享状态
    private static bool ambientRunning;            // 环境协程是否在跑
    private static int tipState;                   // 0=空闲 1=淡出 2=淡入
    private static float tipFadeElapsed;
    private static float tipTimer;
    private static int lastTipIndex;

    private static bool loaded;

    /// <summary>远端是否有新版本（阶段 2 检查出来，点击阶段用来显示金字）。</summary>
    /// <summary>
    /// 异步步骤（Routine）可以设这个来**自己决定这一行的最终文字/颜色**。
    /// ⚠️ 没有它的话，外层循环会把协程写好的文字覆盖成 "{Label} (100%)" 绿色 ——
    ///    下载失败的红字就是这么被吃掉的。
    /// </summary>
    private static string? routineFinalText;
    private static Color routineFinalColor = Color.white;

    /// <summary>
    /// 加载期间报出来的所有红字（左下角那一摞）。
    /// 顺序 = 打印顺序，index 0 在最下面，往后**往上摞**。
    /// </summary>
    private static readonly List<TextMeshPro> errorTexts = new();

    /// <summary>当前 SplashManager（ReportError 需要在任意时机都能建字）。</summary>
    private static SplashManager? currentInstance;

    private static bool newVersionFound;
    private static string newVersionText = "";
    private static bool cachedDoneLoadingRefData;

    /// <summary>外部设置当前进度文本（兼容旧接口）。</summary>
    public static string LoadingText
    {
        set
        {
            try
            {
                if (currentStepText != null) currentStepText.text = value;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[LoadPatch.set]", ex);
            }
        }
    }

    // ======================= 轻量动画辅助 =======================

    /// <summary>手写缓动：驱动一个值平稳到达目标（InOutSine）。</summary>
    private sealed class Tweener
    {
        public float From;
        public float To;
        public float Duration;
        public float Elapsed;
        public Action<float>? Set;    // 每帧写入
        public Action<float>? OnDone; // 完成回调（传入终值）

        public bool Update()
        {
            Elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(Elapsed / Mathf.Max(0.0001f, Duration));
            float eased = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * t); // InOutSine
            float v = Mathf.Lerp(From, To, eased);
            Set?.Invoke(v);
            if (t >= 1f)
            {
                OnDone?.Invoke(v);
                return true;
            }
            return false;
        }
    }

    private static void CleanupTweens()
    {
        for (int i = moveTweens.Count - 1; i >= 0; i--)
        {
            if (moveTweens[i].Update())
                moveTweens.RemoveAt(i);
        }
    }


    public static bool Prefix(SplashManager __instance)
    {
        // ⚠️⚠️ 原来这一行**在 try 外面**，而且直接解引用 LightPlugin.LightSettingsData。
        //    那个字段是在 LightPlugin.Load() 里、**Harmony.PatchAll() 之后**才赋值的，
        //    所以只要 Load() 在那之前抛了异常（实测就是我上一轮加的日志过滤补丁干的），
        //    这里就 NRE —— 异常**逃出 prefix**、打断 SplashManager.Update，
        //    表现为「卡在启动页进不去 + 每帧刷一条 NRE」。
        //    现在：null 安全（读不到就按"不跳过"处理），并且整段包进 try。
        try
        {
            if (LightPlugin.LightSettingsData?.SkipLoadAnimation == true) return true;
        }
        catch { }

        try
        {
            cachedDoneLoadingRefData |= __instance.doneLoadingRefdata;
            __instance.doneLoadingRefdata = false;

            if (cachedDoneLoadingRefData
                && !__instance.startedSceneLoad
                && Time.time - __instance.startTime > Mathf.Max(__instance.minimumSecondsBeforeSceneChange, 1f)
                && !loaded)
            {
                loaded = true;
                __instance.StartCoroutine(CoLoadLight(__instance).WrapToIl2Cpp());
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Postfix(SplashManager __instance)
    {
        try
        {
            __instance.doneLoadingRefdata = cachedDoneLoadingRefData;
        }
        catch (Exception)
        {
        }
    }

    // ======================= 独立环境协程（LOGO 浮动 + 光斑移动/分裂 + 提示轮换） =======================

    /// <summary>每帧驱动 LOGO 浮动、背景光斑漂移/晃动/分裂、提示文字轮换。
    /// 与主流程协程相互独立，任何 yield 都不影响它，保证移动平滑不卡顿。</summary>
    private static IEnumerator CoAmbient()
    {
        while (ambientRunning)
        {
            // LOGO 浮动
            if (logo != null)
            {
                logo.transform.localPosition = LogoPos + new Vector3(
                    0f,
                    Mathf.Sin(Time.time / LogoFloatCycle * Mathf.PI * 2f) * LogoFloatAmp,
                    0f);
            }

            // 背景光斑漂移/晃动/旋转 + 分裂子光斑
            for (int b = 0; b < blobAnims.Count; b++)
            {
                var a = blobAnims[b];
                float t = Time.time / a.cycle * Mathf.PI * 2f + a.phase;
                float scaleT = Time.time / a.scaleCycle * Mathf.PI * 2f + a.scalePhase;

                // 主漂移 + 不规则晃动（双频率叠加）
                var pos = a.origin + new Vector3(
                    Mathf.Sin(t) * 0.8f + Mathf.Sin(t * 1.7f + a.phase2) * 0.3f,
                    Mathf.Cos(t * 1.13f) * 0.5f + Mathf.Cos(t * 2.3f + a.phase2 * 2f) * 0.25f,
                    0f);
                blobs[b].transform.localPosition = pos;
                float s = a.scaleBase * (1f + Mathf.Sin(scaleT) * 0.15f);
                blobs[b].transform.localScale = Vector3.one * s;
                blobs[b].transform.localEulerAngles = new Vector3(0f, 0f, Mathf.Sin(t * 0.7f) * 14f + Mathf.Sin(t * 1.9f + a.phase2) * 6f);

                // 分裂计时
                a.splitTimer += Time.deltaTime;
                if (!a.splitting && a.splitTimer >= a.splitInterval)
                {
                    a.splitTimer = 0f;
                    a.splitting = true;
                    a.splitElapsed = 0f;
                    a.splitStart = pos;
                    var dir = UnityEngine.Random.insideUnitCircle;
                    if (dir.sqrMagnitude < 0.01f) dir = new Vector2(1f, 0.2f);
                    a.splitDir = dir.normalized;
                    a.splitDist = UnityEngine.Random.Range(0.8f, 1.6f);
                }

                // 分裂动画：子光斑从父光斑位置飘散、放大、淡入淡出
                if (a.splitting && subBlobs[b] != null)
                {
                    a.splitElapsed += Time.deltaTime;
                    float k = Mathf.Clamp01(a.splitElapsed / SplitDuration);
                    var sp = a.splitStart + (Vector3)a.splitDir * (a.splitDist * Mathf.Sin(k * Mathf.PI * 0.5f));
                    subBlobs[b].transform.localPosition = sp;
                    subBlobs[b].transform.localScale = Vector3.one * (0.6f + k * 0.8f);
                    float sa = Mathf.Sin(k * Mathf.PI); // 0→1→0
                    subBlobs[b].color = new Color(a.color.r, a.color.g, a.color.b, a.color.a * sa * 0.8f);
                    if (k >= 1f) a.splitting = false;
                }
            }

            // 提示文字轮换（状态机，不 yield 暂停）
            if (tipText != null)
            {
                tipTimer += Time.deltaTime;
                if (tipState == 0 && tipTimer >= TipInterval && TipTexts.Length > 1)
                {
                    tipTimer = 0f;
                    tipState = 1;
                    tipFadeElapsed = 0f;
                }
                if (tipState == 1)
                {
                    tipFadeElapsed += Time.deltaTime;
                    float k = Mathf.Clamp01(tipFadeElapsed / TipFade);
                    tipText.alpha = 0.8f * (1f - k);
                    if (k >= 1f)
                    {
                        int newIdx;
                        do { newIdx = UnityEngine.Random.Range(0, TipTexts.Length); } while (newIdx == lastTipIndex && TipTexts.Length > 1);
                        lastTipIndex = newIdx;
                        tipText.text = TipTexts[newIdx];
                        tipState = 2;
                        tipFadeElapsed = 0f;
                    }
                }
                else if (tipState == 2)
                {
                    tipFadeElapsed += Time.deltaTime;
                    float k = Mathf.Clamp01(tipFadeElapsed / TipFade);
                    tipText.alpha = 0.8f * k;
                    if (k >= 1f) tipState = 0;
                }
            }

            yield return null;
        }
    }

    // ======================= 主协程 =======================

    private static IEnumerator CoLoadLight(SplashManager instance)
    {
        // （原来这里记了 startTime 给"至少 6 秒"用，现在没有硬性下限了，不需要）

        // ReportError 要在任意时机都能建字，所以把 instance 存下来
        currentInstance = instance;

        // ---- 1. 背景光斑（淡金 Bloom，偏左上 + 晃动）+ 分裂子光斑 ----
        var blobSprite = CreateBlobSprite(256);
        for (int i = 0; i < BlobCount; i++)
        {
            var sr = UnityHelper.CreateObject<SpriteRenderer>($"LightBlob{i}", null,
                new Vector3(
                    UnityEngine.Random.Range(-4.4f, -1.6f),
                    UnityEngine.Random.Range(0.6f, 2.8f),
                    -8f));
            sr.sprite = blobSprite;
            var blobColor = UnityEngine.Random.value < 0.5f ? BlobColorA : BlobColorB;
            float a = UnityEngine.Random.Range(0.12f, BlobAlpha);
            sr.color = new Color(
                blobColor.r * UnityEngine.Random.Range(0.9f, 1f),
                blobColor.g * UnityEngine.Random.Range(0.9f, 1f),
                blobColor.b * UnityEngine.Random.Range(0.9f, 1f),
                a);
            float scale = UnityEngine.Random.Range(3.2f, 5.5f);
            sr.transform.localScale = Vector3.one * scale;
            blobs.Add(sr);
            blobAnims.Add(new BlobAnim
            {
                origin = sr.transform.localPosition,
                cycle = UnityEngine.Random.Range(BlobMinCycle, BlobMaxCycle),
                phase = UnityEngine.Random.Range(0f, Mathf.PI * 2f),
                phase2 = UnityEngine.Random.Range(0f, Mathf.PI * 2f),
                scaleBase = scale,
                scaleCycle = UnityEngine.Random.Range(18f, 34f),
                scalePhase = UnityEngine.Random.Range(0f, Mathf.PI * 2f),
                color = sr.color,
                splitInterval = UnityEngine.Random.Range(SplitMinInterval, SplitMaxInterval),
                splitTimer = UnityEngine.Random.Range(0f, SplitMaxInterval), // 初始错开
            });

            // 分裂子光斑（同纹理、更小、初始隐藏）
            var sub = UnityHelper.CreateObject<SpriteRenderer>($"LightBlobSub{i}", null, sr.transform.localPosition + new Vector3(0f, 0f, -0.05f));
            sub.sprite = blobSprite;
            sub.transform.localScale = Vector3.one * 0.6f;
            sub.color = new Color(sr.color.r, sr.color.g, sr.color.b, 0f);
            subBlobs.Add(sub);
        }

        // ---- 2. LOGO ----
        logo = UnityHelper.CreateObject<SpriteRenderer>("LightLogo", null, LogoPos);
        // LOGO 缩放（调这里：0.35 原始大小 → 0.45 放大）
        logo.transform.localScale = Vector3.one * 0.5f;

        var texture = GraphicsHelper.LoadTextureFromResources("Light.Resources.Lobby.LightInDark.png");
        if (texture != null)
        {
            var logoSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            logo.sprite = logoSprite;
        }
        logo.color = new Color(1f, 1f, 1f, 0f);

        // ---- 3. 启动独立环境协程（LOGO 浮动 + 光斑 + 提示，全程平滑） ----
        ambientRunning = true;
        instance.StartCoroutine(CoAmbient().WrapToIl2Cpp());

        // LOGO 淡入
        yield return FadeAlpha(logo, 0f, 1f, LogoFadeIn);
        logo.color = Color.white;

        // ---- 4. 进度文本（锚点，含百分比同行）+ 提示 + 版本 ----
        currentStepText = CreateText(instance, "LoadStepText", StepAnchor, FontStyles.Bold, 1f, TextAlignmentOptions.Center);
        currentStepText.text = "";
        currentStepText.color = new Color(0f, 0f, 0f, 0f);

        tipText = CreateText(instance, "LoadTipText", TipPos, FontStyles.Italic, 0.7f, TextAlignmentOptions.Center);
        tipText.color = new Color(0.6f, 0.6f, 0.6f, 0f);

        versionText = CreateText(instance, "LoadVersionText", VersionPos, FontStyles.Italic, 0.55f, TextAlignmentOptions.BottomRight);
        versionText.color = new Color(0.5f, 0.5f, 0.5f, 0.6f);
        versionText.text = $"{LightPlugin.VisualVersion}";

        // 提示首句随机并初始淡入
        lastTipIndex = UnityEngine.Random.Range(0, TipTexts.Length);
        tipText.text = TipTexts[lastTipIndex];
        tipText.alpha = 0f;
        tipState = 2; // 淡入中
        tipFadeElapsed = 0f;
        tipTimer = TipInterval;

        // ---- 5. 执行**真实**加载步骤（分三个阶段） ----
        //
        // 阶段（用户明确要求）：
        //   1. MOD 加载
        //   2. Tool 检查与下载
        //   3. 应用 Tool 功能
        // 然后进入"点击屏幕继续"阶段。
        //
        // 显示方式：**一条从 0% 走到 100%，然后换下一条**；
        // 阶段切换时先插一行**金色大字**作为阶段标题。
        var steps = BuildSteps();

        string lastPhase = "";
        for (int stepIdx = 0; stepIdx < steps.Count; stepIdx++)
        {
            var step = steps[stepIdx];

            // ---- 阶段标题（金色大字，也走"下移让位"的老路） ----
            if (step.Phase != lastPhase)
            {
                lastPhase = step.Phase;
                yield return EmitPhaseHeader(instance, step.Phase);
            }

            // ---- 本步开始 ----
            currentStepText.text = $"{step.Label} (0%)";
            currentStepText.color = ProgressGray;
            yield return FadeAlpha(currentStepText, 0f, 1f, FadeInDuration);

            // ---- 干活 ----
            bool ok = true;

            if (step.Routine != null)
            {
                // 异步步骤（比如带真实进度的下载）：进度文本由它自己写。
                //
                // ⚠️ 不能写成 `try { yield return step.Routine(); } catch { }` —— C# 禁止
                //    在带 catch 的 try 里 yield（CS1626）。所以改成**手动驱动**：
                //    MoveNext 放在 try 里（它自己不 yield），yield 放在外面。
                var routine = step.Routine();
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = routine.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        LightLogger.LogError($"[Load] 步骤「{step.Label}」失败", ex);
                        break;
                    }

                    if (!moved) break;

                    CleanupTweens();
                    yield return routine.Current;
                }
            }
            else
            {
                // 同步步骤：干完再爬 0→100
                float workStart = Time.realtimeSinceStartup;
                try
                {
                    step.Work?.Invoke();
                }
                catch (Exception ex)
                {
                    ok = false;
                    LightLogger.LogError($"[Load] 步骤「{step.Label}」失败", ex);
                    // 通用兜底：任何步骤炸了都进左下角红字堆，不用每个步骤各写一遍
                    ReportError($"{step.Label} 失败：{ex.Message}");
                }
                float workTook = Time.realtimeSinceStartup - workStart;

                // 干活本身多半是瞬时的（查目录、解析字体都不到 1ms），所以给本步一个短暂的
                // 爬升时间，让"这一条走完了"看得见。
                // ⚠️ 这不是假进度：活儿已经真干完了，爬升纯粹是本步的**显示时长**；
                //    真慢的步骤会把它用掉（ramp 减去 workTook），不会再额外多等。
                float ramp = Mathf.Max(0f, StepRampTime - workTook);
                float rampElapsed = 0f;
                while (rampElapsed < ramp)
                {
                    rampElapsed += Time.deltaTime;
                    float p = Mathf.Clamp01(rampElapsed / ramp);
                    currentStepText.text = $"{step.Label} ({Mathf.RoundToInt(p * 100f)}%)" + (ok ? "" : "  [失败]");
                    currentStepText.color = Color.Lerp(ProgressGray, ok ? ProgressGreen : FailRed, p);
                    CleanupTweens();
                    yield return null;
                }
            }

            if (routineFinalText != null)
            {
                // 协程自己定了最终文字（比如下载失败的红字），别覆盖它
                currentStepText.text = routineFinalText;
                currentStepText.color = routineFinalColor;
                routineFinalText = null;
            }
            else
            {
                currentStepText.text = $"{step.Label} (100%)" + (ok ? "" : "  [失败]");
                currentStepText.color = ok ? ProgressGreen : FailRed;
            }

            // 步骤完成后停一下，让眼睛看清是哪一步
            yield return new WaitForSeconds(CompletedHold);

            // 完成项缩小下移；旧完成项整体下移让位
            completedSteps.Add(currentStepText);
            AnimateCompletedStep(currentStepText);
            float moveElapsed = 0f;
            while (moveElapsed < MoveDuration)
            {
                moveElapsed += Time.deltaTime;
                CleanupTweens();
                yield return null;
            }
            CleanupTweens();

            // 若还有下一项，创建新锚点文本（淡入交给下一轮循环）
            if (stepIdx < steps.Count - 1)
            {
                currentStepText = CreateText(instance, "LoadStepText", StepAnchor, FontStyles.Bold, 1f, TextAlignmentOptions.Center);
                currentStepText.text = "";
                currentStepText.color = new Color(0f, 0f, 0f, 0f);
            }
        }

        // ---- 5.5 等游戏**自己**的加载完成 ----
        //
        // ⚠️ 这里**不再有 MinLoadTime 硬性下限**。
        //    旧代码是 `while (!cachedDoneLoadingRefData || Time.time - startTime < MinLoadTime)`，
        //    哪怕模组 0.2 秒就干完了，也要硬等满 6 秒 —— 那正是用户说的"最低加载时间也是[假的]"。
        //    现在：我们的活儿干完 + 游戏自己加载完，就立刻走。
        if (!cachedDoneLoadingRefData)
        {
            currentStepText.text = "正在等待游戏加载... (100%)";
            currentStepText.color = ProgressGray;
            yield return FadeAlpha(currentStepText, 0f, 1f, FadeInDuration);

            while (!cachedDoneLoadingRefData)
            {
                CleanupTweens();
                yield return null;
            }
        }

        // ---- 6. 步骤整体同时上移并淡出（摞在一起的文字作为一个整体，不逐个） ----
        Vector3 vanishPos = StepAnchor; // 完成项消失处，加载成功将直接出现在这里
        if (completedSteps.Count > 0)
        {
            Vector3[] froms = new Vector3[completedSteps.Count];
            float[] scales = new float[completedSteps.Count];
            for (int i = 0; i < completedSteps.Count; i++)
            {
                froms[i] = completedSteps[i].transform.localPosition;
                scales[i] = completedSteps[i].transform.localScale.x;
            }
            float up = 0.9f; // 整体上移量（顶部回到锚点附近）
            float duration = 0.25f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                for (int i = 0; i < completedSteps.Count; i++)
                {
                    if (completedSteps[i] == null) continue;
                    completedSteps[i].transform.localPosition = Vector3.Lerp(froms[i], froms[i] + new Vector3(0f, up, 0f), t);
                    completedSteps[i].transform.localScale = Vector3.one * Mathf.Lerp(scales[i], 0.1f, t);
                    completedSteps[i].alpha = Mathf.Lerp(1f, 0f, t);
                }
                yield return null;
            }
            // 记录消失处（最新完成项即最上方项上移后的位置）
            if (completedSteps.Count > 0 && completedSteps[completedSteps.Count - 1] != null)
                vanishPos = completedSteps[completedSteps.Count - 1].transform.localPosition;
            foreach (var c in completedSteps) if (c != null) UnityEngine.Object.Destroy(c.gameObject);
            completedSteps.Clear();
        }

        // ---- 7. 绿色"加载成功！"：直接出现在完成项消失处 → 下移到点击位置 → 淡出 ----
        successText = CreateText(instance, "LoadSuccessText", vanishPos, FontStyles.Bold, 1.15f, TextAlignmentOptions.Center);
        successText.text = LoadSuccessTextCN;
        successText.color = ProgressGreen;
        successText.alpha = 0f;
        yield return FadeAlpha(successText, 0f, 1f, SuccessFadeIn);
        yield return new WaitForSeconds(SuccessHold);
        // 下移到"按下任意键继续"的位置（与现在一致 = ClickPos）
        yield return MoveText(successText, vanishPos, ClickPos, 0.5f);
        // 在该位置淡出
        yield return FadeAlpha(successText, 1f, 0f, 0.4f);
        UnityEngine.Object.Destroy(successText.gameObject);
        successText = null;

        // ---- 8. 点击进入 ----
        clickText = CreateText(instance, "LoadClickText", ClickPos, FontStyles.Bold, 0.9f, TextAlignmentOptions.Center);
        clickText.text = ClickToEnterTextCN;
        clickText.color = new Color(ClickGold.r, ClickGold.g, ClickGold.b, 0f);

        // ---- 8.5 有新版本 → 在"点击任意处"**下面**写一行发光金字 ----
        TextMeshPro? newVerText = null;
        if (newVersionFound && !string.IsNullOrEmpty(newVersionText))
        {
            newVerText = CreateText(instance, "LoadNewVersionText", NewVersionPos,
                FontStyles.Bold, 0.8f, TextAlignmentOptions.Center);
            newVerText.text = newVersionText;
            newVerText.color = new Color(ClickGold.r, ClickGold.g, ClickGold.b, 0f);

            // 发光感：TMP 描边 + 呼吸透明度（描边比换材质便宜，也不用碰字体图集）
            try
            {
                newVerText.outlineWidth = 0.25f;
                newVerText.outlineColor = new Color(ClickGold.r, ClickGold.g, ClickGold.b, 0.7f);
            }
            catch { }

            LightLogger.Log($"[Load] 显示新版本提示：{newVersionText}");
        }

        bool entered = false;
        while (!entered)
        {
            // 点击提示：平滑正弦呼吸（cos 参数连续增长，无取模跳变）
            float breathe = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * Time.time / ClickGlowCycle);
            clickText.alpha = 0.4f + breathe * 0.6f;

            // 新版本金字：跟着呼吸（发光的观感）
            if (newVerText != null)
                newVerText.alpha = 0.55f + breathe * 0.45f;

            // ---- 按 Ctrl → 打开 GitHub 最新 Release（**只在检测到新版本时响应**）----
            if (newVersionFound &&
                (Input.GetKeyDown(KeyCode.LeftControl) || Input.GetKeyDown(KeyCode.RightControl)))
            {
                try
                {
                    LightLogger.Log($"[Load] 按下 Ctrl → 打开 {LightToolManager.GitHubLatestUrl}");
                    Application.OpenURL(LightToolManager.GitHubLatestUrl);
                }
                catch (Exception ex)
                {
                    LightLogger.LogWarning($"[Load] 打开链接失败：{ex.Message}");
                }
            }

            if (Input.GetMouseButtonDown(0) || Input.touchCount > 0)
            {
                entered = true;
                break;
            }
            yield return null;
        }

        // ---- 9. 停止环境协程并整体淡出 ----
        ambientRunning = false;
        yield return null; // 等环境协程退出

        // 淡出顺序：光斑 → 提示 → 版本 → 点击提示 → **新版本金字（倒数第三）**
        //           → **红字堆（倒数第二）** → LOGO（最后）
        //   红字堆一条一条消失（从最下面那条开始），用户要求它是倒数第二个。
        //   新版本金字是后加的：它原本**不在这个序列里**，所以进游戏前一直挂在屏幕上不消失，
        //   用户报「那个文字不会淡出」。现在插在 clickText 之后 → 倒数第三个。
        for (int i = 0; i < blobs.Count; i++)
            yield return FadeAlpha(blobs[i], blobs[i].color.a, 0f, 0.4f);
        yield return FadeAlpha(tipText, tipText.alpha, 0f, 0.4f);
        yield return FadeAlpha(versionText, versionText.alpha, 0f, 0.4f);
        yield return FadeAlpha(clickText, clickText.alpha, 0f, 0.4f);

        // ⚠️ 只有检测到新版本时 newVerText 才非 null（见上面 8.5 段），所以必须判空
        if (newVerText != null)
            yield return FadeAlpha(newVerText, newVerText.alpha, 0f, 0.4f);

        foreach (var err in errorTexts)
            if (err != null)
                yield return FadeAlpha(err, err.alpha, 0f, 0.3f);

        yield return FadeAlpha(logo, 1f, 0f, 0.4f);

        foreach (var b in blobs) if (b != null) UnityEngine.Object.Destroy(b.gameObject);
        foreach (var s in subBlobs) if (s != null) UnityEngine.Object.Destroy(s.gameObject);
        blobs.Clear(); subBlobs.Clear(); blobAnims.Clear();
        if (logo != null) UnityEngine.Object.Destroy(logo.gameObject);
        if (tipText != null) UnityEngine.Object.Destroy(tipText.gameObject);
        if (versionText != null) UnityEngine.Object.Destroy(versionText.gameObject);
        if (clickText != null) UnityEngine.Object.Destroy(clickText.gameObject);
        if (newVerText != null) UnityEngine.Object.Destroy(newVerText.gameObject);
        foreach (var t in completedSteps) if (t != null) UnityEngine.Object.Destroy(t.gameObject);
        completedSteps.Clear();
        foreach (var e in errorTexts) if (e != null) UnityEngine.Object.Destroy(e.gameObject);
        errorTexts.Clear();
        currentInstance = null;
        if (currentStepText != null) UnityEngine.Object.Destroy(currentStepText.gameObject);
        logo = null; tipText = null; versionText = null; clickText = null; currentStepText = null;

        // 保留原有进入游戏的调用接口
        instance.sceneChanger.AllowFinishLoadingScene();
        instance.startedSceneLoad = true;
    }

    // ======================= 小工具 =======================

    private sealed class BlobAnim
    {
        public Vector3 origin;
        public float cycle;
        public float phase;
        public float phase2;
        public float scaleBase;
        public float scaleCycle;
        public float scalePhase;
        public Color color;             // 光斑颜色（子光斑继承）
        public float splitInterval;     // 分裂间隔
        public float splitTimer;
        public bool splitting;
        public float splitElapsed;
        public Vector3 splitStart;
        public Vector2 splitDir;
        public float splitDist;
    }

    // ⚠️ StepDuration / LoadStepCount 是旧"假进度条"的遗留物，已经没用了。
    //    （旧实现按整体时间把 6 秒摊到每段文字上 —— 和真实工作无关。）
    //    LoadStepCount 现在由 BuildSteps() 在运行时决定。

    private static TextMeshPro CreateText(SplashManager instance, string name, Vector3 pos, FontStyles style, float fontSizeScale, TextAlignmentOptions align)
    {
        var tmp = UnityEngine.Object.Instantiate(instance.errorPopup.InfoText, null);
        tmp.name = name;
        tmp.transform.localPosition = pos;
        tmp.fontStyle = style;
        tmp.fontSize *= fontSizeScale;
        tmp.alignment = align;
        tmp.gameObject.SetActive(true);
        return tmp;
    }

    /// <summary>径向渐变光斑 Sprite（中心 1 → 边缘 0，柔和光晕）。</summary>
    private static Sprite? CreateBlobSprite(int size)
    {
        try
        {
            var tex = new Texture2D(size, size);
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - r) / r;
                    float dy = (y + 0.5f - r) / r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d >= 1f) { tex.SetPixel(x, y, new Color(1f, 1f, 1f, 0f)); continue; }
                    float a = 1f - d;
                    a = a * a * (3f - 2f * a); // smoothstep，柔和边缘
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[LoadPatch.CreateBlobSprite]", ex);
            return null;
        }
    }

    // ---- 动画片段 ----

    private static IEnumerator FadeAlpha(Component target, float from, float to, float duration)
    {
        if (target == null) yield break;
        var r = target as TextMeshPro;
        var s = target as SpriteRenderer;
        if (r != null)
        {
            r.alpha = from;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                r.alpha = Mathf.Lerp(from, to, t);
                yield return null;
            }
            r.alpha = to;
        }
        else if (s != null)
        {
            var c = s.color;
            c.a = from;
            s.color = c;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                var cc = s.color;
                cc.a = Mathf.Lerp(from, to, t);
                s.color = cc;
                yield return null;
            }
            var fc = s.color; fc.a = to; s.color = fc;
        }
    }

    private static IEnumerator MoveText(TextMeshPro tmp, Vector3 from, Vector3 to, float duration)
    {
        if (tmp == null) yield break;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float e = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * t);
            tmp.transform.localPosition = Vector3.Lerp(from, to, e);
            yield return null;
        }
        tmp.transform.localPosition = to;
    }

    /// <summary>完成项：缩小 0.5 并下移；所有旧完成项整体下移让位。
    /// 间距按尺度区分：新完成项（刚从全尺寸缩下来）用 FirstRowGap 拉开与首行的距离；
    /// 旧完成项（都是缩小到 0.5 的小字）彼此用 RowGap 收紧，避免空洞。</summary>
    private static void AnimateCompletedStep(TextMeshPro done)
    {
        // 新完成项从当前位置下移（全尺寸 → 完成区，间距稍宽）
        AddTextMove(done, done.transform.localPosition + new Vector3(0f, -FirstRowGap, 0f));
        AddTextScale(done, 0.5f);

        // 旧完成项整体下移让位（小字之间间距收紧）
        for (int i = 0; i < completedSteps.Count - 1; i++)
        {
            var old = completedSteps[i];
            AddTextMove(old, old.transform.localPosition + new Vector3(0f, -RowGap, 0f));
        }
    }

    private static void AddTextMove(TextMeshPro? tmp, Vector3 target)
    {
        if (tmp == null) return;
        Vector3 from = tmp.transform.localPosition;
        moveTweens.Add(new Tweener
        {
            From = 0f, To = 1f, Duration = MoveDuration,
            Set = v => tmp.transform.localPosition = Vector3.Lerp(from, target, v),
            OnDone = _ => tmp.transform.localPosition = target,
        });
    }

    private static void AddTextScale(TextMeshPro? tmp, float targetScale)
    {
        if (tmp == null) return;
        float from = tmp.transform.localScale.x;
        moveTweens.Add(new Tweener
        {
            From = 0f, To = 1f, Duration = MoveDuration,
            Set = v => tmp.transform.localScale = Vector3.one * Mathf.Lerp(from, targetScale, v),
            OnDone = _ => tmp.transform.localScale = Vector3.one * targetScale,
        });
    }
}