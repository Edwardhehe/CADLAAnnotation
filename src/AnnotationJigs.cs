using System;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.GraphicsInterface;
using CadPolyline = ZwSoft.ZwCAD.DatabaseServices.Polyline;
#else
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using CadPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
#endif

namespace LAAnnotation
{
    /// <summary>拖拽预览——第一阶段：拖拽出云线覆盖范围（矩形/菱形/椭圆）。</summary>
    internal sealed class RegionPreviewJig : DrawJig
    {
        private readonly Point3d _first;private readonly AnnotationSettings _settings;private Point3d _current;
        public Point3d Current=>_current;
        public RegionPreviewJig(Point3d first,AnnotationSettings settings){_first=first;_current=first;_settings=settings;}
        protected override SamplerStatus Sampler(JigPrompts prompts){var o=new JigPromptPointOptions("\n指定批注范围另一个角点: "){UseBasePoint=true,BasePoint=_first,Cursor=CursorType.RubberBand};var r=prompts.AcquirePoint(o);if(r.Status!=PromptStatus.OK)return SamplerStatus.Cancel;if(r.Value.DistanceTo(_current)<1e-8)return SamplerStatus.NoChange;_current=r.Value;return SamplerStatus.OK;}
        protected override bool WorldDraw(WorldDraw draw){if(_first.DistanceTo(_current)<1e-8)return true;using(var cloud=AnnotationService.BuildCloud(ToMin(_first,_current),ToMax(_first,_current),_settings)){cloud.Elevation=_first.Z;draw.Geometry.Draw(cloud);}return true;}
        internal static Point2d ToMin(Point3d a,Point3d b)=>new Point2d(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y));internal static Point2d ToMax(Point3d a,Point3d b)=>new Point2d(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
    }

    /// <summary>拖拽预览——第二阶段：在云线范围基础上拖拽文字框位置，实时显示引线和矩形框。</summary>
    internal sealed class PlacementPreviewJig : DrawJig
    {
        private readonly Point3d _first,_second;private readonly AnnotationSettings _settings;private Point3d _current;
        public Point3d Current=>_current;
        public PlacementPreviewJig(Point3d first,Point3d second,AnnotationSettings settings){_first=first;_second=second;_settings=settings;_current=second;}
        protected override SamplerStatus Sampler(JigPrompts prompts){var o=new JigPromptPointOptions("\n指定批注框位置: "){UseBasePoint=true,BasePoint=_second,Cursor=CursorType.RubberBand};var r=prompts.AcquirePoint(o);if(r.Status!=PromptStatus.OK)return SamplerStatus.Cancel;if(r.Value.DistanceTo(_current)<1e-8)return SamplerStatus.NoChange;_current=r.Value;return SamplerStatus.OK;}
        protected override bool WorldDraw(WorldDraw draw)
        {
            var min=RegionPreviewJig.ToMin(_first,_second);var max=RegionPreviewJig.ToMax(_first,_second);
            using(var cloud=AnnotationService.BuildCloud(min,max,_settings)){cloud.Elevation=_first.Z;draw.Geometry.Draw(cloud);}
            var attach=AnnotationService.ClosestCorner(min,max,_current);using(var leader=new CadPolyline()){leader.AddVertexAt(0,attach,0,0,0);leader.AddVertexAt(1,new Point2d(_current.X,attach.Y),0,0,0);leader.AddVertexAt(2,new Point2d(_current.X,_current.Y),0,0,0);leader.Elevation=_current.Z;draw.Geometry.Draw(leader);}
            var width=_settings.FixedWidth?_settings.FixedWidthValue:Math.Max(_settings.TextHeight*18,55);var height=_settings.TextHeight*5;
            using(var box=AnnotationService.BuildBox(_current,width,height)){box.Elevation=_current.Z;draw.Geometry.Draw(box);}return true;
        }
    }
}
