using System;
using System.Collections.Generic;
using System.Threading;
using Engine;
using Game;
using SuMod;
using SuMod.Tools;

namespace StringInterceptor
{
    /// <summary>
    /// 字符串处理接口 — 外部 Mod 可注册此接口实现自定义文字处理（如翻译）
    /// </summary>
    public interface IStringProcessor
    {
        string Process(string key, string original, int index);
    }

    /// <summary>
    /// 默认实现：在字符串前加顺序编号
    /// </summary>
    public class DefaultStringProcessor : IStringProcessor
    {
        public string Process(string key, string original, int index)
        {
            return $"[{index}] {original}";
        }
    }

    /// <summary>
    /// 文字拦截器 Mod — 拦截 StringsManager 和 Widget 树中的所有字符串，通过 IStringProcessor 回调处理
    /// </summary>
    public class StringInterceptorMod : IMod
    {
        public string Name => "String Interceptor";
        public string Version => "1.2.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;

        private IModParentField _mpf;
        private readonly List<IStringProcessor> _processors = new List<IStringProcessor>();
        private Timer _widgetScanTimer;
        private readonly HashSet<LabelWidget> _processedLabels = new HashSet<LabelWidget>();
        private readonly HashSet<ButtonWidget> _processedButtons = new HashSet<ButtonWidget>();
        private int _globalIndex;
        private long _lastScannedFrame = -1;

        // 自适应扫描频率
        private int _framesSinceNewWidget;      // 连续多少帧没发现新 Widget
        private const int IDLE_THRESHOLD = 60;  // 60帧（~1秒）无新 Widget → 降频
        private int _currentIntervalMs = 16;    // 当前 Timer 间隔

        public void RegisterProcessor(IStringProcessor processor)
        {
            if (processor != null && !_processors.Contains(processor))
                _processors.Add(processor);
        }

        public void UnregisterProcessor(IStringProcessor processor)
        {
            _processors.Remove(processor);
        }

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            _mpf = Program.ModManager.ModParentField;
            RegisterProcessor(new DefaultStringProcessor());

            eventBus.SubscribeEvent("Loading.Initialize", args =>
            {
                return HandleLoadingInitialize((List<Action>)args[0]);
            }, EventPriority.LOWEST);

            Log.Information("[StringInterceptor] v1.2.0 Loaded. Adaptive per-frame scanning.");
        }

        private object[] HandleLoadingInitialize(List<Action> actions)
        {
            actions.Add(() =>
            {
                try { ProcessStrings(); }
                catch (Exception ex) { Log.Error($"[StringInterceptor] ProcessStrings failed: {ex.Message}"); }
            });

            actions.Add(() =>
            {
                try { StartWidgetScanner(); }
                catch (Exception ex) { Log.Error($"[StringInterceptor] StartWidgetScanner failed: {ex.Message}"); }
            });

            return new object[] { false, actions };
        }

        private void ProcessStrings()
        {
            var strings = _mpf.GetStaticField<Dictionary<string, string>>(typeof(StringsManager), "m_strings");
            if (strings == null || strings.Count == 0)
            {
                Log.Warning("[StringInterceptor] m_strings is null or empty.");
                return;
            }

            var keys = new List<string>(strings.Keys);
            foreach (var key in keys)
            {
                string original = strings[key];
                string result = original;
                foreach (var processor in _processors)
                {
                    try { result = processor.Process(key, result, ++_globalIndex); }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] Processor failed for key '{key}': {ex.Message}"); }
                }
                strings[key] = result;
            }

            Log.Information($"[StringInterceptor] Processed {strings.Count} StringsManager entries. Index at {_globalIndex}.");
        }

        /// <summary>
        /// 启动定时器，自适应频率扫描 Widget 树
        /// 活跃期：16ms（每帧），捕获新 Widget 无闪烁
        /// 空闲期：200ms，降低遍历开销
        /// </summary>
        private void StartWidgetScanner()
        {
            _widgetScanTimer = new Timer(_ =>
            {
                try { Dispatcher.Dispatch(() => ScanWidgetTree()); }
                catch { }
            }, null, _currentIntervalMs, _currentIntervalMs);

            Log.Information("[StringInterceptor] Widget scanner started (adaptive interval).");
        }

        private void ScanWidgetTree()
        {
            long frame = Time.FrameIndex;
            if (frame == _lastScannedFrame) return;
            _lastScannedFrame = frame;

            var root = ScreensManager.RootWidget;
            if (root == null) return;

            int labelCount = 0;
            int buttonCount = 0;
            ScanContainer(root, ref labelCount, ref buttonCount);

            bool foundNew = labelCount > 0 || buttonCount > 0;

            // 自适应频率
            if (foundNew)
            {
                _framesSinceNewWidget = 0;
                if (_currentIntervalMs != 16)
                {
                    _currentIntervalMs = 16;
                    _widgetScanTimer?.Change(16, 16);
                }
                Log.Information($"[StringInterceptor] Frame {frame}: {labelCount} labels, {buttonCount} buttons. Back to 16ms.");
            }
            else
            {
                _framesSinceNewWidget++;
                if (_framesSinceNewWidget >= IDLE_THRESHOLD && _currentIntervalMs != 200)
                {
                    _currentIntervalMs = 200;
                    _widgetScanTimer?.Change(200, 200);
                    Log.Information("[StringInterceptor] Idle for 60 frames, slowing to 200ms.");
                }
            }

            // 清理已销毁 Widget 的引用，同时如果有清理说明界面在变化，保持活跃
            int removedLabels = _processedLabels.RemoveWhere(l => l.ParentWidget == null);
            int removedButtons = _processedButtons.RemoveWhere(b => b.ParentWidget == null);
            if (removedLabels > 0 || removedButtons > 0)
            {
                // Widget 销毁意味着界面在变化，重置空闲计数
                _framesSinceNewWidget = 0;
                if (_currentIntervalMs != 16)
                {
                    _currentIntervalMs = 16;
                    _widgetScanTimer?.Change(16, 16);
                }
            }
        }

        private void ScanContainer(ContainerWidget container, ref int labelCount, ref int buttonCount)
        {
            if (container == null) return;

            foreach (var child in container.Children)
            {
                if (child is LabelWidget label && !_processedLabels.Contains(label))
                {
                    _processedLabels.Add(label);
                    string original = label.Text;
                    if (!string.IsNullOrEmpty(original))
                    {
                        string source = child.GetType().Name;
                        string result = original;
                        foreach (var processor in _processors)
                        {
                            try { result = processor.Process(source, result, ++_globalIndex); }
                            catch { }
                        }
                        label.Text = result;
                        labelCount++;
                    }
                }
                else if (child is ButtonWidget button && !_processedButtons.Contains(button))
                {
                    _processedButtons.Add(button);
                    string original = button.Text;
                    if (!string.IsNullOrEmpty(original))
                    {
                        string source = child.GetType().Name;
                        string result = original;
                        foreach (var processor in _processors)
                        {
                            try { result = processor.Process(source, result, ++_globalIndex); }
                            catch { }
                        }
                        button.Text = result;
                        buttonCount++;
                    }
                }

                if (child is ContainerWidget childContainer)
                {
                    ScanContainer(childContainer, ref labelCount, ref buttonCount);
                }
            }
        }

        public void OnUnload()
        {
            _widgetScanTimer?.Dispose();
            _widgetScanTimer = null;
            _processedLabels.Clear();
            _processedButtons.Clear();
            _processors.Clear();
            Log.Information("[StringInterceptor] Unloaded.");
        }
    }
}
