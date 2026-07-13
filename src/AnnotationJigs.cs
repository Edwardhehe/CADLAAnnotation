using System;
using System.Linq;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.GraphicsInterface;
using CadPolyline = ZwSoft.ZwCAD.DatabaseServices.Polyline;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using CadPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
#endif

namespace LAAnnotation
{
    /// <summary>拖拽预览：在 UCS 局部坐标中创建云线，整体变换到 WCS 显示。</summary>
    internal sealed class RegionPreviewJig : DrawJig
    {
        private readonly Matrix3d _ucsToWcs,_wcsToUcs;private readonly Point3d _first;private readonly AnnotationSettings _settings;private Point3d _current;
        public Point3d Current=>_current;
        public RegionPreviewJig(Document doc,Point3d first,AnnotationSettings settings){_ucsToWcs=AnnotationService.GetUcsMatrix(doc);_wcsToUcs=_ucsToWcs.Inverse();_first=first;_current=first;_settings=settings;}
        protected override SamplerStatus Sampler(JigPrompts prompts){var o=new JigPromptPointOptions("\n指定批注范围另一个角点: "){UseBasePoint=true,BasePoint=_first,Cursor=CursorType.RubberBand};var r=prompts.AcquirePoint(o);if(r.Status!=PromptStatus.OK)return SamplerStatus.Cancel;if(r.Value.DistanceTo(_current)<1e-8)return SamplerStatus.NoChange;_current=r.Value;return SamplerStatus.OK;}
        protected override bool WorldDraw(WorldDraw draw)
        {
            var first=_first.TransformBy(_wcsToUcs);var current=_current.TransformBy(_wcsToUcs);if(first.DistanceTo(current)<1e-8)return true;
            using(var cloud=AnnotationService.BuildCloud(ToMin(first,current),ToMax(first,current),_settings)){cloud.Elevation=first.Z;cloud.TransformBy(_ucsToWcs);draw.Geometry.Draw(cloud);}return true;
        }
        internal static Point2d ToMin(Point3d a,Point3d b)=>new Point2d(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y));internal static Point2d ToMax(Point3d a,Point3d b)=>new Point2d(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
    }

    /// <summary>拖拽预览：整套局部批注几何一次性从 UCS 变换到 WCS。</summary>
    internal sealed class PlacementPreviewJig : DrawJig
    {
        private readonly Matrix3d _ucsToWcs,_wcsToUcs;private readonly Point3d _first,_second;private readonly AnnotationSettings _settings;private Point3d _current;
        public Point3d Current=>_current;
        public PlacementPreviewJig(Document doc,Point3d first,Point3d second,AnnotationSettings settings){_ucsToWcs=AnnotationService.GetUcsMatrix(doc);_wcsToUcs=_ucsToWcs.Inverse();_first=first;_second=second;_settings=settings;_current=second;}
        protected override SamplerStatus Sampler(JigPrompts prompts){var o=new JigPromptPointOptions("\n指定批注框位置: "){UseBasePoint=true,BasePoint=_second,Cursor=CursorType.RubberBand};var r=prompts.AcquirePoint(o);if(r.Status!=PromptStatus.OK)return SamplerStatus.Cancel;if(r.Value.DistanceTo(_current)<1e-8)return SamplerStatus.NoChange;_current=r.Value;return SamplerStatus.OK;}
        protected override bool WorldDraw(WorldDraw draw)
        {
            var first=_first.TransformBy(_wcsToUcs);var second=_second.TransformBy(_wcsToUcs);var current=_current.TransformBy(_wcsToUcs);
            var min=RegionPreviewJig.ToMin(first,second);var max=RegionPreviewJig.ToMax(first,second);
            var width=_settings.FixedWidth?_settings.FixedWidthValue:Math.Max(_settings.TextHeight*18,55);var height=_settings.TextHeight*5;
            var cloudCorner=AnnotationService.ClosestCorner(min,max,current);var boxCorners=new[]{new Point2d(current.X,current.Y),new Point2d(current.X+width,current.Y),new Point2d(current.X+width,current.Y+height),new Point2d(current.X,current.Y+height)};
            var boxCorner=boxCorners.OrderBy(c=>c.GetDistanceTo(cloudCorner)).First();
            using(var cloud=AnnotationService.BuildCloud(min,max,_settings)){cloud.Elevation=first.Z;cloud.TransformBy(_ucsToWcs);draw.Geometry.Draw(cloud);}
            using(var leader=new CadPolyline()){leader.AddVertexAt(0,cloudCorner,0,0,0);leader.AddVertexAt(1,boxCorner,0,0,0);leader.Elevation=current.Z;leader.TransformBy(_ucsToWcs);draw.Geometry.Draw(leader);}
            using(var box=AnnotationService.BuildBox(current,width,height)){box.Elevation=current.Z;box.TransformBy(_ucsToWcs);draw.Geometry.Draw(box);}return true;
        }
    }
}
