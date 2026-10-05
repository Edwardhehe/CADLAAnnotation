using System;
using System.Collections.Generic;
using System.Linq;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.GraphicsInterface;
using CadPolyline = ZwSoft.ZwCAD.DatabaseServices.Polyline;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using CadPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
#endif

namespace GMAnnotation
{
    /// <summary>
    /// 拖拽预览的公共工具。
    /// <para>AutoCAD 的 Jig 在每次鼠标移动时（且可能每帧多次）回调 WorldDraw，这里必须只做纯几何计算：
    /// 不开事务、不取当前视图（Editor.GetCurrentView）、不建 MText 实测。最外层事务结束（含 Abort）时 AutoCAD 会刷新图形，
    /// 在拖拽中这会把拖拽图形擦掉，表现为预览"时隐时现、几乎看不见"（AutoCAD 2020~2024 尤其明显，中望不刷新所以不受影响）。
    /// 所以与光标无关的量（视图方向、文字框尺寸、已确定的云线）都在 Jig 构造时、拖拽开始前一次算好。</para>
    /// </summary>
    internal static class RegionPreviewDrawing
    {
        /// <summary>把已确认的历史区域一次性构造成显示用云线（WCS，已定向、已设预览外观）。</summary>
        internal static List<CadPolyline> BuildHistory(
            Document doc,
            Matrix3d ucsToWcs,
            Matrix3d wcsToUcs,
            AnnotationSettings source,
            IList<Point3d> firsts,
            IList<Point3d> seconds,
            Vector3d? viewDirection)
        {
            var result = new List<CadPolyline>();
            if (firsts == null || seconds == null) return result;
            for (var i = 0; i < firsts.Count && i < seconds.Count; i++)
            {
                var a = firsts[i].TransformBy(wcsToUcs);
                var b = seconds[i].TransformBy(wcsToUcs);
                var diagonal = Math.Sqrt(
                    Math.Pow(b.X - a.X, 2) +
                    Math.Pow(b.Y - a.Y, 2));
                var settings = AnnotationService.ResolveEffectiveSettings(
                    doc,
                    source,
                    null,
                    diagonal);
                var cloud = AnnotationService.BuildCloud(
                    RegionPreviewJig.ToMin(a, b),
                    RegionPreviewJig.ToMax(a, b),
                    settings);
                AnnotationService.ApplyPreviewAppearance(
                    cloud,
                    settings,
                    settings.CloudColor,
                    "cloud");
                cloud.Elevation = a.Z;
                cloud.TransformBy(ucsToWcs);
                AnnotationService.OrientCloudBulgesForView(cloud, viewDirection);
                result.Add(cloud);
            }
            return result;
        }

        internal static void DrawAll(WorldDraw draw, IEnumerable<CadPolyline> entities)
        {
            foreach (var entity in entities) draw.Geometry.Draw(entity);
        }

        internal static void DisposeAll(List<CadPolyline> entities)
        {
            if (entities == null) return;
            foreach (var entity in entities)
            {
                try { entity?.Dispose(); } catch { }
            }
            entities.Clear();
        }

        private static int _failureLogged;

        /// <summary>WorldDraw 内部异常只记一次日志（异常会让 CAD 丢掉这一帧的预览，便于用户反馈时定位）。</summary>
        internal static void LogFailure(string jig, System.Exception ex)
        {
            if (System.Threading.Interlocked.Exchange(ref _failureLogged, 1) == 0)
                PluginLog.Warning("Jig." + jig, "预览绘制异常（仅记录首次）：" + ex.Message);
        }
    }

    /// <summary>多对一第一角点预览：显示全部已确认区域，并允许回车结束。</summary>
    internal sealed class RegionFirstPointPreviewJig : DrawJig, IDisposable
    {
        private readonly List<CadPolyline> _history;
        private Point3d _current;
        private bool _hasSample;

        public Point3d Current => _current;
        public bool FinishRequested { get; private set; }

        public RegionFirstPointPreviewJig(
            Document doc,
            AnnotationSettings source,
            IList<Point3d> firsts,
            IList<Point3d> seconds)
        {
            var ucsToWcs = AnnotationService.GetUcsMatrix(doc);
            _history = RegionPreviewDrawing.BuildHistory(
                doc,
                ucsToWcs,
                ucsToWcs.Inverse(),
                source,
                firsts,
                seconds,
                AnnotationService.CurrentViewDirection(doc));
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(
                "\n指定下一个云线范围第一个角点，回车/空格结束: ")
            {
                UserInputControls = UserInputControls.Accept3dCoordinates |
                                    UserInputControls.NullResponseAccepted
            };
            var result = prompts.AcquirePoint(options);

            if (result.Status == PromptStatus.None)
            {
                FinishRequested = true;
                return SamplerStatus.Cancel;
            }

            if (result.Status != PromptStatus.OK)
            {
                return SamplerStatus.Cancel;
            }

            if (_hasSample && result.Value.DistanceTo(_current) < 1e-8)
            {
                return SamplerStatus.NoChange;
            }

            _current = result.Value;
            _hasSample = true;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            try { RegionPreviewDrawing.DrawAll(draw, _history); }
            catch (System.Exception ex) { RegionPreviewDrawing.LogFailure("RegionFirstPoint", ex); }
            return true;
        }

        public void Dispose() => RegionPreviewDrawing.DisposeAll(_history);
    }

    /// <summary>拖拽预览：按当前区域尺寸解析最终参数，在 UCS 中构造并变换到 WCS。</summary>
    internal sealed class RegionPreviewJig : DrawJig, IDisposable
    {
        private readonly Document _doc;private readonly Matrix3d _ucsToWcs,_wcsToUcs;private readonly Point3d _first;private readonly AnnotationSettings _source;private readonly List<CadPolyline> _history;private readonly Vector3d? _viewDirection;private Point3d _current;
        public Point3d Current=>_current;
        public RegionPreviewJig(Document doc,Point3d first,AnnotationSettings source,IList<Point3d> historyFirsts=null,IList<Point3d> historySeconds=null)
        {
            _doc=doc;_ucsToWcs=AnnotationService.GetUcsMatrix(doc);_wcsToUcs=_ucsToWcs.Inverse();_first=first;_current=first;_source=source;
            _viewDirection=AnnotationService.CurrentViewDirection(doc);
            _history=RegionPreviewDrawing.BuildHistory(doc,_ucsToWcs,_wcsToUcs,source,historyFirsts,historySeconds,_viewDirection);
        }
        // 保留 BasePoint 以支持相对坐标和对象捕捉，但使用普通十字光标，避免 CAD 额外画出角点对角虚线。
        protected override SamplerStatus Sampler(JigPrompts prompts){var o=new JigPromptPointOptions("\n指定批注范围另一个角点: "){UseBasePoint=true,BasePoint=_first,Cursor=CursorType.Crosshair};var r=prompts.AcquirePoint(o);if(r.Status!=PromptStatus.OK)return SamplerStatus.Cancel;if(r.Value.DistanceTo(_current)<1e-8)return SamplerStatus.NoChange;_current=r.Value;return SamplerStatus.OK;}
        protected override bool WorldDraw(WorldDraw draw)
        {
            try
            {
                RegionPreviewDrawing.DrawAll(draw,_history);
                var first=_first.TransformBy(_wcsToUcs);var current=_current.TransformBy(_wcsToUcs);var w=Math.Abs(current.X-first.X);var h=Math.Abs(current.Y-first.Y);if(w<1e-8||h<1e-8)return true;
                var settings=AnnotationService.ResolveEffectiveSettings(_doc,_source,new AnnotationData(),Math.Sqrt(w*w+h*h));
                using(var cloud=AnnotationService.BuildCloud(ToMin(first,current),ToMax(first,current),settings)){AnnotationService.ApplyPreviewAppearance(cloud,settings,settings.CloudColor,"cloud");cloud.Elevation=first.Z;cloud.TransformBy(_ucsToWcs);AnnotationService.OrientCloudBulgesForView(cloud,_viewDirection);draw.Geometry.Draw(cloud);}
            }
            catch(System.Exception ex){RegionPreviewDrawing.LogFailure("Region",ex);}
            return true;
        }
        public void Dispose()=>RegionPreviewDrawing.DisposeAll(_history);
        internal static Point2d ToMin(Point3d a,Point3d b)=>new Point2d(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y));internal static Point2d ToMax(Point3d a,Point3d b)=>new Point2d(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
    }

    internal enum PlacementGeometryKind { Region, Polygon, MultiRegion }

    /// <summary>完整批注放置预览：实际云线、文字边框和全部引线。
    /// 云线与文字框尺寸跟光标位置无关，构造时一次算好；WorldDraw 每帧只重算文字框位置与引线。</summary>
    internal sealed class PlacementPreviewJig : DrawJig, IDisposable
    {
        private readonly Matrix3d _ucsToWcs,_wcsToUcs;private readonly AnnotationSettings _settings;private readonly PlacementGeometryKind _kind;private Point3d _current;
        /// <summary>UCS 中的云线（只用于求引线锚点，不绘制）。</summary>
        private readonly List<CadPolyline> _localClouds=new List<CadPolyline>();
        /// <summary>显示用云线（WCS，已定向、已设预览外观）。</summary>
        private readonly List<CadPolyline> _displayClouds=new List<CadPolyline>();
        private readonly List<AnnotationSettings> _cloudSettings=new List<AnnotationSettings>();
        private readonly List<Point2d> _mins=new List<Point2d>(),_maxs=new List<Point2d>();
        private readonly double _boxW,_boxH;
        public Point3d Current=>_current;
        public PlacementPreviewJig(
            Document doc,
            AnnotationSettings settings,
            AnnotationSettings source,
            IList<Point3d> firsts,
            IList<Point3d> seconds,
            IList<Point3d> polygon,
            Point3d initial,
            PlacementGeometryKind kind)
        {
            _settings = settings;
            source = source ?? settings;
            _current = initial;
            _kind = kind;
            _ucsToWcs = AnnotationService.GetUcsMatrix(doc);
            _wcsToUcs = _ucsToWcs.Inverse();
            var viewDirection = AnnotationService.CurrentViewDirection(doc);
            try
            {
                if (kind == PlacementGeometryKind.Polygon)
                {
                    var pts = (polygon ?? new List<Point3d>()).Select(p => p.TransformBy(_wcsToUcs)).ToList();
                    if (pts.Count >= 3)
                    {
                        var cloud = AnnotationService.BuildPolygonCloud(pts.Select(p => new Point2d(p.X, p.Y)).ToList(), settings);
                        cloud.Elevation = pts[0].Z;
                        AddCloud(cloud, settings, new Point2d(), new Point2d(), viewDirection);
                    }
                }
                else if (firsts != null && seconds != null)
                {
                    for (var i = 0; i < firsts.Count && i < seconds.Count; i++)
                    {
                        var a = firsts[i].TransformBy(_wcsToUcs); var b = seconds[i].TransformBy(_wcsToUcs);
                        var min = RegionPreviewJig.ToMin(a, b); var max = RegionPreviewJig.ToMax(a, b);
                        var cloudSettings = kind == PlacementGeometryKind.MultiRegion
                            ? AnnotationService.ResolveEffectiveSettings(doc, source, new AnnotationData(), Math.Sqrt(Math.Pow(max.X - min.X, 2) + Math.Pow(max.Y - min.Y, 2)))
                            : settings;
                        var cloud = AnnotationService.BuildCloud(min, max, cloudSettings);
                        cloud.Elevation = a.Z;
                        AddCloud(cloud, cloudSettings, min, max, viewDirection);
                    }
                }
            }
            catch (System.Exception ex) { PluginLog.Warning("Jig.Placement", "预览云线构造失败：" + ex.Message); }

            // 预览外框与正式创建同一套估算，避免放置时框偏小、落图后才「撑开」的观感落差。
            // MText 实测要开事务，只能在拖拽开始前做一次（见 RegionPreviewDrawing 说明）。
            var previewData = new AnnotationData
            {
                Content = "批注内容",
                DrawingNo = "图号",
                Status = "待处理",
                Date = DateTime.Now.ToString("yyyy-MM-dd"),
                Discipline = "专业",
                Author = "批注人"
            };
            var requestedWidth = settings.FixedWidth
                ? settings.FixedWidthValue
                : Math.Max(settings.TextHeight * 18, 55);
            AnnotationService.MeasureTextBox(doc, previewData, settings, requestedWidth, out _boxW, out _boxH);
        }

        private void AddCloud(CadPolyline local, AnnotationSettings cloudSettings, Point2d min, Point2d max, Vector3d? viewDirection)
        {
            var display = (CadPolyline)local.Clone();
            AnnotationService.ApplyPreviewAppearance(display, cloudSettings, cloudSettings.CloudColor, "cloud");
            display.TransformBy(_ucsToWcs);
            AnnotationService.OrientCloudBulgesForView(display, viewDirection);
            _localClouds.Add(local); _displayClouds.Add(display); _cloudSettings.Add(cloudSettings); _mins.Add(min); _maxs.Add(max);
        }

        // 放置阶段已由 WorldDraw 绘制云线/框/引出线，勿用 RubberBand，否则会从原点额外拉出一条对角虚线。
        protected override SamplerStatus Sampler(JigPrompts prompts){var o=new JigPromptPointOptions("\n指定批注框位置: "){Cursor=CursorType.Crosshair};var r=prompts.AcquirePoint(o);if(r.Status!=PromptStatus.OK)return SamplerStatus.Cancel;if(r.Value.DistanceTo(_current)<1e-8)return SamplerStatus.NoChange;_current=r.Value;return SamplerStatus.OK;}
        protected override bool WorldDraw(WorldDraw draw)
        {
            try
            {
                var localText=_current.TransformBy(_wcsToUcs);
                using (var box = AnnotationService.BuildBox(localText, _boxW, _boxH))
                {
                    var boxCorners = new[]
                    {
                        new Point2d(localText.X, localText.Y),
                        new Point2d(localText.X + _boxW, localText.Y),
                        new Point2d(localText.X + _boxW, localText.Y + _boxH),
                        new Point2d(localText.X, localText.Y + _boxH)
                    };
                    box.Elevation = localText.Z;

                    for (var i = 0; i < _displayClouds.Count; i++)
                    {
                        var cloudSettings = _cloudSettings[i];
                        draw.Geometry.Draw(_displayClouds[i]);

                        Point2d anchor;
                        if (_kind == PlacementGeometryKind.Polygon)
                        {
                            var closest = _localClouds[i].GetClosestPointTo(localText, false);
                            anchor = new Point2d(closest.X, closest.Y);
                        }
                        else anchor = AnnotationService.ResolveRegionLeaderAnchor(_localClouds[i], _mins[i], _maxs[i], cloudSettings, localText);

                        using (var leader = new CadPolyline())
                        {
                            leader.AddVertexAt(0, anchor, 0, 0, 0);
                            leader.AddVertexAt(1, boxCorners.OrderBy(c => c.GetDistanceTo(anchor)).First(), 0, 0, 0);
                            leader.Elevation = localText.Z;
                            AnnotationService.ApplyPreviewAppearance(leader, cloudSettings, cloudSettings.LeaderColor, "leader");
                            leader.TransformBy(_ucsToWcs);
                            draw.Geometry.Draw(leader);
                        }
                    }

                    AnnotationService.ApplyPreviewAppearance(
                        box,
                        _settings,
                        _settings.SameColors
                            ? _settings.CloudColor
                            : _settings.BoxColor,
                        "box");
                    box.TransformBy(_ucsToWcs);
                    draw.Geometry.Draw(box);
                }
            }
            catch (System.Exception ex) { RegionPreviewDrawing.LogFailure("Placement", ex); }
            return true;
        }

        public void Dispose()
        {
            RegionPreviewDrawing.DisposeAll(_localClouds);
            RegionPreviewDrawing.DisposeAll(_displayClouds);
        }
    }

    internal sealed class PlinePointPreviewJig : DrawJig
    {
        private readonly Document _doc;
        private readonly Matrix3d _ucsToWcs;
        private readonly Matrix3d _wcsToUcs;
        private readonly IList<Point3d> _points;
        private readonly AnnotationSettings _source;
        private readonly Vector3d? _viewDirection;
        private Point3d _current;
        private bool _hasSample;

        public Point3d Current => _current;
        public bool FinishRequested { get; private set; }

        public PlinePointPreviewJig(
            Document doc,
            IList<Point3d> points,
            AnnotationSettings source)
        {
            _doc = doc;
            _points = points;
            _source = source;
            _ucsToWcs = AnnotationService.GetUcsMatrix(doc);
            _wcsToUcs = _ucsToWcs.Inverse();
            _viewDirection = AnnotationService.CurrentViewDirection(doc);
            _current = points.Count > 0
                ? points[points.Count - 1]
                : Point3d.Origin;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            string prompt;
            if (_points.Count == 0)
            {
                prompt = "\n指定 PL 云线起点: ";
            }
            else if (_points.Count < 3)
            {
                prompt = $"\n指定下一点（已输入 {_points.Count} 点，至少需要 3 点）: ";
            }
            else
            {
                prompt = $"\n指定下一点（已输入 {_points.Count} 点，回车/空格完成）: ";
            }

            var options = new JigPromptPointOptions(prompt)
            {
                Cursor = CursorType.RubberBand,
                UserInputControls = UserInputControls.Accept3dCoordinates |
                                    UserInputControls.NullResponseAccepted
            };
            if (_points.Count > 0)
            {
                options.UseBasePoint = true;
                options.BasePoint = _points[_points.Count - 1];
            }

            var result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.None)
            {
                FinishRequested = true;
                return SamplerStatus.Cancel;
            }

            if (result.Status != PromptStatus.OK)
            {
                return SamplerStatus.Cancel;
            }

            if (_hasSample && result.Value.DistanceTo(_current) < 1e-8)
            {
                return SamplerStatus.NoChange;
            }

            _current = result.Value;
            _hasSample = true;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            try { DrawPreview(draw); }
            catch (System.Exception ex) { RegionPreviewDrawing.LogFailure("PlinePoint", ex); }
            return true;
        }

        private void DrawPreview(WorldDraw draw)
        {
            if (_points.Count == 0)
            {
                return;
            }

            var local = _points
                .Concat(new[] { _current })
                .Select(point => point.TransformBy(_wcsToUcs))
                .ToList();
            var polygon = local
                .Select(point => new Point2d(point.X, point.Y))
                .ToList();
            var minX = local.Min(point => point.X);
            var maxX = local.Max(point => point.X);
            var minY = local.Min(point => point.Y);
            var maxY = local.Max(point => point.Y);
            var diagonal = Math.Sqrt(
                Math.Pow(maxX - minX, 2) +
                Math.Pow(maxY - minY, 2));
            // 对角线为 0（只有一个点）时不能传 0：ResolveEffectiveSettings 会退回去取当前视图，拖拽中不要做这种事。
            var settings = AnnotationService.ResolveEffectiveSettings(
                _doc,
                _source,
                null,
                diagonal > 1e-9 ? diagonal : 1e-6);

            if (polygon.Count >= 3 &&
                AnnotationService.ValidateCloudPolygon(polygon, out var ignored))
            {
                using (var cloud = AnnotationService.BuildPolygonCloud(
                    polygon,
                    settings))
                {
                    cloud.Elevation = local[0].Z;
                    AnnotationService.ApplyPreviewAppearance(
                        cloud,
                        settings,
                        settings.CloudColor,
                        "cloud");
                    cloud.TransformBy(_ucsToWcs);
                    AnnotationService.OrientCloudBulgesForView(cloud, _viewDirection);
                    draw.Geometry.Draw(cloud);
                }
            }
            else
            {
                using (var line = new CadPolyline())
                {
                    for (var i = 0; i < polygon.Count; i++)
                    {
                        line.AddVertexAt(i, polygon[i], 0, 0, 0);
                    }

                    line.Elevation = local[0].Z;
                    AnnotationService.ApplyPreviewAppearance(
                        line,
                        settings,
                        settings.CloudColor,
                        "cloud");
                    line.TransformBy(_ucsToWcs);
                    draw.Geometry.Draw(line);
                }
            }
        }
    }

    internal sealed class MoveAnnotationPreviewJig : DrawJig
    {
        private readonly Point3d _basePoint;
        private readonly List<Entity> _movableEntities;
        private readonly List<CadPolyline> _leaders;
        private Point3d _current;
        private bool _hasSample;

        public Point3d Current => _current;

        public MoveAnnotationPreviewJig(
            Point3d basePoint,
            IEnumerable<Entity> movableEntities,
            IEnumerable<CadPolyline> leaders)
        {
            _basePoint = basePoint;
            _current = basePoint;
            _movableEntities = movableEntities.ToList();
            _leaders = leaders.ToList();
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            // 移动预览已自绘文字框与引出线，改用十字光标避免 CAD 再画一条橡皮筋虚线。
            var options = new JigPromptPointOptions(
                "\n指定批注文字框新位置: ")
            {
                UseBasePoint = true,
                BasePoint = _basePoint,
                Cursor = CursorType.Crosshair
            };
            var result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK)
            {
                return SamplerStatus.Cancel;
            }

            if (_hasSample && result.Value.DistanceTo(_current) < 1e-8)
            {
                return SamplerStatus.NoChange;
            }

            _current = result.Value;
            _hasSample = true;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            var displacement = _current - _basePoint;
            var transform = Matrix3d.Displacement(displacement);

            foreach (var source in _movableEntities)
            {
                using (var preview = source.Clone() as Entity)
                {
                    if (preview == null)
                    {
                        continue;
                    }

                    preview.TransformBy(transform);
                    draw.Geometry.Draw(preview);
                }
            }

            foreach (var source in _leaders)
            {
                using (var preview = source.Clone() as CadPolyline)
                {
                    if (preview == null)
                    {
                        continue;
                    }

                    var anchor = preview.GetPoint2dAt(0);
                    preview.TransformBy(transform);
                    preview.SetPointAt(0, anchor);
                    draw.Geometry.Draw(preview);
                }
            }

            return true;
        }
    }

}
