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

namespace LAAnnotation
{
    internal static class RegionPreviewDrawing
    {
        internal static void DrawHistory(
            WorldDraw draw,
            Document doc,
            Matrix3d ucsToWcs,
            Matrix3d wcsToUcs,
            AnnotationSettings source,
            IList<Point3d> firsts,
            IList<Point3d> seconds)
        {
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

                using (var cloud = AnnotationService.BuildCloud(
                    RegionPreviewJig.ToMin(a, b),
                    RegionPreviewJig.ToMax(a, b),
                    settings))
                {
                    AnnotationService.ApplyPreviewAppearance(
                        cloud,
                        settings,
                        settings.CloudColor,
                        "cloud");
                    cloud.Elevation = a.Z;
                    cloud.TransformBy(ucsToWcs);
                    draw.Geometry.Draw(cloud);
                }
            }
        }
    }

    /// <summary>多对一第一角点预览：显示全部已确认区域，并允许回车结束。</summary>
    internal sealed class RegionFirstPointPreviewJig : DrawJig
    {
        private readonly Document _doc;
        private readonly Matrix3d _ucsToWcs;
        private readonly Matrix3d _wcsToUcs;
        private readonly AnnotationSettings _source;
        private readonly List<Point3d> _firsts;
        private readonly List<Point3d> _seconds;
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
            _doc = doc;
            _source = source;
            _firsts = firsts?.ToList() ?? new List<Point3d>();
            _seconds = seconds?.ToList() ?? new List<Point3d>();
            _ucsToWcs = AnnotationService.GetUcsMatrix(doc);
            _wcsToUcs = _ucsToWcs.Inverse();
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
            RegionPreviewDrawing.DrawHistory(
                draw,
                _doc,
                _ucsToWcs,
                _wcsToUcs,
                _source,
                _firsts,
                _seconds);
            return true;
        }
    }

    /// <summary>拖拽预览：按当前区域尺寸解析最终参数，在 UCS 中构造并变换到 WCS。</summary>
    internal sealed class RegionPreviewJig : DrawJig
    {
        private readonly Document _doc;private readonly Matrix3d _ucsToWcs,_wcsToUcs;private readonly Point3d _first;private readonly AnnotationSettings _source;private readonly List<Point3d> _historyFirsts,_historySeconds;private Point3d _current;
        public Point3d Current=>_current;
        public RegionPreviewJig(Document doc,Point3d first,AnnotationSettings source,IList<Point3d> historyFirsts=null,IList<Point3d> historySeconds=null){_doc=doc;_ucsToWcs=AnnotationService.GetUcsMatrix(doc);_wcsToUcs=_ucsToWcs.Inverse();_first=first;_current=first;_source=source;_historyFirsts=historyFirsts?.ToList()??new List<Point3d>();_historySeconds=historySeconds?.ToList()??new List<Point3d>();}
        protected override SamplerStatus Sampler(JigPrompts prompts){var o=new JigPromptPointOptions("\n指定批注范围另一个角点: "){UseBasePoint=true,BasePoint=_first,Cursor=CursorType.RubberBand};var r=prompts.AcquirePoint(o);if(r.Status!=PromptStatus.OK)return SamplerStatus.Cancel;if(r.Value.DistanceTo(_current)<1e-8)return SamplerStatus.NoChange;_current=r.Value;return SamplerStatus.OK;}
        protected override bool WorldDraw(WorldDraw draw)
        {
            RegionPreviewDrawing.DrawHistory(draw,_doc,_ucsToWcs,_wcsToUcs,_source,_historyFirsts,_historySeconds);
            var first=_first.TransformBy(_wcsToUcs);var current=_current.TransformBy(_wcsToUcs);var w=Math.Abs(current.X-first.X);var h=Math.Abs(current.Y-first.Y);if(w<1e-8||h<1e-8)return true;
            var settings=AnnotationService.ResolveEffectiveSettings(_doc,_source,new AnnotationData(),Math.Sqrt(w*w+h*h));
            using(var cloud=AnnotationService.BuildCloud(ToMin(first,current),ToMax(first,current),settings)){AnnotationService.ApplyPreviewAppearance(cloud,settings,settings.CloudColor,"cloud");cloud.Elevation=first.Z;cloud.TransformBy(_ucsToWcs);draw.Geometry.Draw(cloud);}return true;
        }
        internal static Point2d ToMin(Point3d a,Point3d b)=>new Point2d(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y));internal static Point2d ToMax(Point3d a,Point3d b)=>new Point2d(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
    }

    internal enum PlacementGeometryKind { Region, Polygon, MultiRegion }

    /// <summary>完整批注放置预览：实际云线、文字、边框和全部引线。</summary>
    internal sealed class PlacementPreviewJig : DrawJig
    {
        private readonly Document _doc;private readonly Matrix3d _ucsToWcs,_wcsToUcs;private readonly AnnotationSettings _settings,_source;private readonly PlacementGeometryKind _kind;private readonly List<Point3d> _firsts,_seconds,_polygon;private Point3d _current;
        public Point3d Current=>_current;
        public PlacementPreviewJig(Document doc,AnnotationSettings settings,AnnotationSettings source,IList<Point3d> firsts,IList<Point3d> seconds,IList<Point3d> polygon,Point3d initial,PlacementGeometryKind kind){_doc=doc;_settings=settings;_source=source??settings;_firsts=firsts?.ToList()??new List<Point3d>();_seconds=seconds?.ToList()??new List<Point3d>();_polygon=polygon?.ToList()??new List<Point3d>();_current=initial;_kind=kind;_ucsToWcs=AnnotationService.GetUcsMatrix(doc);_wcsToUcs=_ucsToWcs.Inverse();}
        protected override SamplerStatus Sampler(JigPrompts prompts){var o=new JigPromptPointOptions("\n指定批注框位置: "){Cursor=CursorType.RubberBand};var r=prompts.AcquirePoint(o);if(r.Status!=PromptStatus.OK)return SamplerStatus.Cancel;if(r.Value.DistanceTo(_current)<1e-8)return SamplerStatus.NoChange;_current=r.Value;return SamplerStatus.OK;}
        protected override bool WorldDraw(WorldDraw draw)
        {
            var localText=_current.TransformBy(_wcsToUcs);var clouds=new List<CadPolyline>();var anchors=new List<Point2d>();
            try
            {
                if(_kind==PlacementGeometryKind.Polygon)
                {
                    var pts=_polygon.Select(p=>p.TransformBy(_wcsToUcs)).ToList();var cloud=AnnotationService.BuildPolygonCloud(pts.Select(p=>new Point2d(p.X,p.Y)).ToList(),_settings);cloud.Elevation=pts[0].Z;clouds.Add(cloud);anchors.Add(new Point2d(cloud.GetClosestPointTo(localText,false).X,cloud.GetClosestPointTo(localText,false).Y));
                }
                else for(var i=0;i<_firsts.Count;i++)
                {
                    var a=_firsts[i].TransformBy(_wcsToUcs);var b=_seconds[i].TransformBy(_wcsToUcs);var min=RegionPreviewJig.ToMin(a,b);var max=RegionPreviewJig.ToMax(a,b);var settings=_kind==PlacementGeometryKind.MultiRegion?AnnotationService.ResolveEffectiveSettings(_doc,_source,new AnnotationData(),Math.Sqrt(Math.Pow(max.X-min.X,2)+Math.Pow(max.Y-min.Y,2))):_settings;var cloud=AnnotationService.BuildCloud(min,max,settings);cloud.Elevation=a.Z;clouds.Add(cloud);anchors.Add(AnnotationService.ResolveRegionLeaderAnchor(cloud,min,max,settings,localText));
                }
                var boxW=_settings.FixedWidth?_settings.FixedWidthValue:Math.Max(_settings.TextHeight*18,55);var boxH=Math.Max(_settings.TextHeight*5,_settings.HeaderHeight+_settings.TextHeight+_settings.SecondLineHeight+_settings.TextHeight*2);
                using(var box=AnnotationService.BuildBox(localText,boxW,boxH))
                {
                    var boxCorners=new[]{new Point2d(localText.X,localText.Y),new Point2d(localText.X+boxW,localText.Y),new Point2d(localText.X+boxW,localText.Y+boxH),new Point2d(localText.X,localText.Y+boxH)};box.Elevation=localText.Z;
                    for(var i=0;i<clouds.Count;i++){var cloudSettings=_kind==PlacementGeometryKind.MultiRegion?AnnotationService.SettingsForRegion(_doc,_source,_firsts[i],_seconds[i]):_settings;AnnotationService.ApplyPreviewAppearance(clouds[i],cloudSettings,cloudSettings.CloudColor,"cloud");clouds[i].TransformBy(_ucsToWcs);draw.Geometry.Draw(clouds[i]);using(var leader=new CadPolyline()){leader.AddVertexAt(0,anchors[i],0,0,0);leader.AddVertexAt(1,boxCorners.OrderBy(c=>c.GetDistanceTo(anchors[i])).First(),0,0,0);leader.Elevation=localText.Z;AnnotationService.ApplyPreviewAppearance(leader,cloudSettings,cloudSettings.LeaderColor,"leader");leader.TransformBy(_ucsToWcs);draw.Geometry.Draw(leader);}}
                    AnnotationService.ApplyPreviewAppearance(box,_settings,_settings.SameColors?_settings.CloudColor:_settings.BoxColor,"box");box.TransformBy(_ucsToWcs);draw.Geometry.Draw(box);
                }
            }
            finally{foreach(var cloud in clouds)cloud.Dispose();}
            return true;
        }
    }

    internal sealed class PlinePointPreviewJig : DrawJig
    {
        private readonly Document _doc;
        private readonly Matrix3d _ucsToWcs;
        private readonly Matrix3d _wcsToUcs;
        private readonly IList<Point3d> _points;
        private readonly AnnotationSettings _source;
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
            if (_points.Count == 0)
            {
                return true;
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
            var settings = AnnotationService.ResolveEffectiveSettings(
                _doc,
                _source,
                null,
                diagonal);

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

            return true;
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
            var options = new JigPromptPointOptions(
                "\n指定批注文字框新位置: ")
            {
                UseBasePoint = true,
                BasePoint = _basePoint,
                Cursor = CursorType.RubberBand
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

    internal sealed class CrossPreviewJig : DrawJig
    {
        private readonly Matrix3d _ucsToWcs,_wcsToUcs;private readonly AnnotationSettings _settings;private Point3d _current;private bool _hasSample;
        public Point3d Current=>_current;
        public CrossPreviewJig(Document doc,AnnotationSettings settings){_settings=settings;_ucsToWcs=AnnotationService.GetUcsMatrix(doc);_wcsToUcs=_ucsToWcs.Inverse();}
        protected override SamplerStatus Sampler(JigPrompts prompts){var r=prompts.AcquirePoint(new JigPromptPointOptions("\n指定十字点位置: "));if(r.Status!=PromptStatus.OK)return SamplerStatus.Cancel;if(_hasSample&&r.Value.DistanceTo(_current)<1e-8)return SamplerStatus.NoChange;_current=r.Value;_hasSample=true;return SamplerStatus.OK;}
        protected override bool WorldDraw(WorldDraw draw){var p=_current.TransformBy(_wcsToUcs);var size=_settings.TextHeight*3;using(var h=new CadPolyline())using(var v=new CadPolyline()){h.AddVertexAt(0,new Point2d(p.X-size,p.Y),0,0,0);h.AddVertexAt(1,new Point2d(p.X+size,p.Y),0,0,0);v.AddVertexAt(0,new Point2d(p.X,p.Y-size),0,0,0);v.AddVertexAt(1,new Point2d(p.X,p.Y+size),0,0,0);h.Elevation=v.Elevation=p.Z;AnnotationService.ApplyPreviewAppearance(h,_settings,_settings.CloudColor,"cloud");AnnotationService.ApplyPreviewAppearance(v,_settings,_settings.CloudColor,"cloud");h.TransformBy(_ucsToWcs);v.TransformBy(_ucsToWcs);draw.Geometry.Draw(h);draw.Geometry.Draw(v);}return true;}
    }
}
