using System;
using LightInDark.Core;
using LightInDark.UI.Window;
using UnityEngine;

namespace Light.UI.Window
{
    /// <summary>
    /// 通用自绘滑条：槽 + 填充 + 手柄 + 不可见抓取盒，鼠标可拖。
    ///
    /// ---- 为什么不复用原版设置界面的音量条 ----
    /// 原版是 <c>SlideBar : PassiveUiElement</c>（实例字段 <c>OptionsMenuBehaviour.MusicSlider</c> /
    /// <c>SoundSlider</c>）。三条硬理由：
    ///   ① **模板拿不到** —— <c>OptionsMenuBehaviour</c> 是普通 MonoBehaviour，不是
    ///      <c>DestroyableSingleton</c>，只能满场景 FindObjectOfType，各场景存在性/active 都不保证；
    ///   ② **克隆体会带持久化监听** —— 预制体里 <c>OnValueChange</c> 指向
    ///      <c>OptionsMenuBehaviour.UpdateMusicVolume</c>，拖我们的条会顺手**改掉游戏自己的
    ///      音乐音量并写盘**（见 AGENTS.md §4.5 同类坑，必须整体替换 OnClick）；
    ///   ③ **换算对不上** —— <c>SlideBar.ReceiveClickDrag</c> 用
    ///      <c>DragPosition - Bar.transform.position</c>（**世界坐标差**）直接当
    ///      <c>Dot.transform.localPosition</c>；而本工程的面板普遍整体缩放过
    ///      （如 <c>BackgroundPanel.PanelScale</c> / 音乐窗口 0.82），世界差 ≠ 本地差
    ///      → 手柄和鼠标必然对不齐（提前满格/拖不到头）。
    ///
    /// ---- 设计 ----
    /// · 完全自绘，**不碰原版预制体**，因此天然没有 ② 那个"改到游戏设置"的风险
    /// · 鼠标映射用的是 <see cref="Transform.InverseTransformPoint"/>（世界 → **本对象本地**），
    ///   **缩放自动被抵消**，所以父级怎么缩放都对得上 —— 这正是原版做法缺的那一步
    /// · 拖动状态机自己维护，不走 <c>PassiveButtonManager</c>（那个每帧对每个控件各自判定、
    ///   完全不看 z，用它反而会被面板外的原版控件抢走点击）
    ///
    /// ⚠️ **未经实机验证**（2026-10-05 新写）。若拖动不跟手，先看日志
    ///    <c>[LightSlider]</c> 那几行确认相机是否找到、命中盒是否被点中。
    /// </summary>
    internal sealed class LightSlider
    {
        // ---- 视觉常量 ----
        private const float TrackH = 0.16f;     // 槽高
        private const float FillH = 0.11f;      // 填充高
        private const float HandleW = 0.13f;    // 手柄宽
        private const float HandleH = 0.26f;    // 手柄高
        private const float HitH = 0.34f;       // 不可见抓取盒高（比视觉大，好点）

        private static readonly Color TrackColor = new(0.14f, 0.13f, 0.11f, 0.92f);
        private static readonly Color FillColor = new(1f, 0.87f, 0.55f, 0.95f);      // 淡金
        private static readonly Color HandleColor = new(1f, 0.93f, 0.75f, 1f);

        private readonly GameObject _root;
        private readonly Transform _tf;
        private readonly float _width;
        private readonly Action<float> _onChanged;

        private SpriteRenderer? _fill;
        private SpriteRenderer? _handle;
        private BoxCollider2D? _hit;
        private Transform? _fillTf;
        private Transform? _handleTf;

        /// <summary>当前值 0~1。</summary>
        public float Value { get; private set; }

        /// <summary>是否正在被拖动。</summary>
        public bool Dragging { get; private set; }

        /// <summary>拖动时锁定的相机 —— **中途绝不换相机**（换了数值会跳）。</summary>
        private Camera? _dragCam;

        private LightSlider(GameObject root, Transform tf, float width, Action<float> onChanged)
        {
            _root = root;
            _tf = tf;
            _width = width;
            _onChanged = onChanged;
        }

        /// <summary>
        /// 创建一条滑条。
        /// </summary>
        /// <param name="parent">父物体。**建议传已经缩放好的面板内容根**，本类不处理缩放。</param>
        /// <param name="name">物体名（调试用）。</param>
        /// <param name="center">滑条中心（父物体本地坐标）。</param>
        /// <param name="width">滑条总宽。</param>
        /// <param name="initial">初值 0~1。</param>
        /// <param name="onChanged">拖动/点击时回调（每帧可能多次）。</param>
        /// <param name="z">同容器内 z 越小越靠前（本工程约定）。</param>
        public static LightSlider? Create(Transform parent, string name, Vector3 center,
            float width, float initial, Action<float> onChanged, float z = -0.1f)
        {
            try
            {
                if (parent == null) return null;
                if (width <= 0.05f) width = 0.05f;

                var go = new GameObject(name);
                // 继承父物体的 layer：本工程"新 GameObject 默认 layer 0 会被别的相机画的东西盖住"
                // 是踩过的坑（见 AGENTS.md §4.3），所以一律跟随父物体。
                go.layer = parent.gameObject.layer;
                go.transform.SetParent(parent, false);
                go.transform.localPosition = center;

                var self = new LightSlider(go, go.transform, width, onChanged);

                self.BuildTrack(z);
                self.BuildFill(z - 0.01f);
                self.BuildHandle(z - 0.02f);
                self.BuildHit(z - 0.03f);

                self.SetValue(initial, notify: false);
                return self;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[LightSlider.Create]", ex);
                return null;
            }
        }

        // ------------------------------------------------------------------
        //  构建
        // ------------------------------------------------------------------

        private void BuildTrack(float z)
        {
            var sr = MakeRect(_tf, "Track", new Vector2(_width, TrackH), new Vector3(0f, 0f, z), TrackColor);
            if (sr != null) sr.drawMode = SpriteDrawMode.Sliced;
        }

        private void BuildFill(float z)
        {
            _fill = MakeRect(_tf, "Fill", new Vector2(_width * Value, FillH), new Vector3(0f, 0f, z), FillColor);
            if (_fill != null) { _fill.drawMode = SpriteDrawMode.Sliced; _fillTf = _fill.transform; }
        }

        private void BuildHandle(float z)
        {
            _handle = MakeRect(_tf, "Handle", new Vector2(HandleW, HandleH), new Vector3(0f, 0f, z), HandleColor);
            if (_handle != null) { _handle.drawMode = SpriteDrawMode.Sliced; _handleTf = _handle.transform; }
        }

        private void BuildHit(float z)
        {
            var go = new GameObject("Hit");
            go.layer = _tf.gameObject.layer;
            go.transform.SetParent(_tf, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);

            _hit = go.AddComponent<BoxCollider2D>();
            _hit.size = new Vector2(_width, HitH);
            _hit.isTrigger = true;      // 只用来做 OverlapPoint 命中，不参与物理
        }

        /// <summary>
        /// 造一个纯色矩形。
        /// ⚠️ sprite 统一用 <see cref="VanillaAsset.FullScreenSprite"/>（自建白图、
        /// 已打 <c>DontUnloadUnusedAsset</c>）—— 原版贴图会在场景卸载后变成"假 null"，
        /// 表现是"框没了但还能点"（见 AGENTS.md §4.6）。
        /// </summary>
        private static SpriteRenderer? MakeRect(Transform parent, string name, Vector2 size,
            Vector3 localPos, Color color)
        {
            try
            {
                var go = new GameObject(name);
                go.layer = parent.gameObject.layer;
                go.transform.SetParent(parent, false);
                go.transform.localPosition = localPos;

                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = VanillaAsset.FullScreenSprite;
                sr.color = color;
                sr.drawMode = SpriteDrawMode.Sliced;
                sr.size = size;
                return sr;
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[LightSlider.MakeRect:{name}] {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------
        //  数值
        // ------------------------------------------------------------------

        /// <summary>外部设值（0~1）。<paramref name="notify"/> = false 时不回调（初始化用）。</summary>
        public void SetValue(float v, bool notify = true)
        {
            try
            {
                float nv = Mathf.Clamp01(v);
                bool changed = !Mathf.Approximately(nv, Value);
                Value = nv;

                // 手柄：把 0~1 映射到"左右各留半个手柄"的区间，这样两端能真的到头
                float half = HandleW * 0.5f;
                float span = Mathf.Max(0.01f, _width - HandleW);
                float x = -span * 0.5f + span * nv;

                if (_handleTf != null) _handleTf.localPosition = new Vector3(x, 0f, _handleTf.localPosition.z);

                if (_fill != null)
                {
                    float fw = Mathf.Max(0.0001f, span * nv + HandleW);   // 从左端到手柄
                    _fill.size = new Vector2(fw, FillH);
                    if (_fillTf != null)
                        _fillTf.localPosition = new Vector3(-_width * 0.5f + fw * 0.5f, 0f, _fillTf.localPosition.z);
                    _fill.enabled = nv > 0.001f;                          // 0 时干脆不画
                }

                if (changed && notify) _onChanged?.Invoke(Value);
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[LightSlider.SetValue] {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        //  每帧：拖动
        // ------------------------------------------------------------------

        /// <summary>面板的每帧输入里调它（鼠标左键按下 → 拖动 → 松开）。</summary>
        public void Tick()
        {
            try
            {
                if (_hit == null) return;

                if (Input.GetMouseButtonDown(0))
                {
                    if (TryHit(out var world))
                    {
                        Dragging = true;
                        ApplyWorld(world);        // "点哪跳哪"
                    }
                }
                else if (Dragging && Input.GetMouseButton(0))
                {
                    // ⚠️ 拖动期间**继续用锁定的那台相机**做换算，不重新找相机 ——
                    //    中途换相机会让同一个鼠标位置换算出不同的世界坐标，表现为"数值乱跳"。
                    if (TryWorldFromMouse(_dragCam, out var world)) ApplyWorld(world);
                }
                else if (Dragging && !Input.GetMouseButton(0))
                {
                    Dragging = false;
                    _dragCam = null;
                    LightLogger.Log($"[LightSlider] {_root.name} 拖动结束，值={Value:F2}");
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[LightSlider.Tick:{_root.name}] {ex.Message}");
            }
        }

        private bool TryHit(out Vector3 world)
        {
            world = Vector3.zero;
            var cam = FindUiCamera(_tf.gameObject.layer);
            if (cam == null) return false;

            if (!TryWorldFromMouse(cam, out world)) return false;
            if (!_hit.OverlapPoint(world)) return false;

            _dragCam = cam;     // 锁定
            return true;
        }

        /// <summary>
        /// 鼠标 → 与滑条同一平面上的世界坐标。
        ///
        /// ⚠️ 不能直接用 <c>ScreenToWorldPoint(mouse.x, mouse.y, 0)</c> —— 那会得到相机近平面上的点，
        ///    与世界空间的 UI 不在一个平面。正确做法：先把滑条自己的世界位置投到屏幕拿到**深度**，
        ///    再用这个深度反投影（透视相机也正确）。
        /// </summary>
        private bool TryWorldFromMouse(Camera? cam, out Vector3 world)
        {
            world = Vector3.zero;
            try
            {
                if (cam == null) return false;
                var self = _tf.position;
                float depth = cam.WorldToScreenPoint(self).z;
                if (depth <= 0f) return false;      // 在相机背后

                var m = Input.mousePosition;
                m.z = depth;
                world = cam.ScreenToWorldPoint(m);
                return true;
            }
            catch { return false; }
        }

        /// <summary>把世界坐标映射成 0~1 并应用。</summary>
        private void ApplyWorld(Vector3 world)
        {
            try
            {
                // ⚠️ 关键：用 InverseTransformPoint 转到**本对象本地**空间。
                //    父级整体缩放（本工程面板普遍缩放）会被自动抵消 —— 这正是原版 SlideBar
                //    用"世界差"做不到的。少这一步就会出现"提前满格 / 拖不到头"。
                float localX = _tf.InverseTransformPoint(world).x;

                float half = HandleW * 0.5f;
                float span = Mathf.Max(0.01f, _width - HandleW);
                float v = (localX - (-span * 0.5f)) / span;
                SetValue(v);
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[LightSlider.ApplyWorld] {ex.Message}");
            }
        }

        /// <summary>
        /// 找"画这一层的相机"。
        /// 本工程不同场景相机配置完全不同（见 AGENTS.md §4.3），所以不能写死 Camera.main。
        /// </summary>
        private static Camera? FindUiCamera(int layer)
        {
            try
            {
                Camera? best = null;
                float bestDepth = float.MinValue;    // ⚠️ Camera.depth 是 float，不能用 int
                var cams = Camera.allCameras;
                if (cams != null)
                {
                    foreach (var c in cams)
                    {
                        if (c == null) continue;
                        if (!c.isActiveAndEnabled) continue;
                        if ((c.cullingMask & (1 << layer)) == 0) continue;   // 不画这一层
                        if (c.depth > bestDepth) { bestDepth = c.depth; best = c; }
                    }
                }
                return best != null ? best : Camera.main;   // 兜底
            }
            catch { return Camera.main; }
        }
    }
}
