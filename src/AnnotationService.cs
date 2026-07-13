using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.Runtime;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
#endif

namespace LAAnnotation
{
    internal enum CloudPromptResult
    {
        Completed,
        Finished,
        Cancelled
    }

    /// <summary>批注核心服务：几何交互、实体创建/读写/更新/删除。</summary>
    internal static class AnnotationService
    {
        /// <summary>CAD 编组名前缀，用于关联批注的所有子实体。</summary>
        private const string GroupPrefix = "LA_PZ_NOTE_";
        /// <summary>XRecord 数据键名，用于在编组扩展字典中存储批注数据。</summary>
        private const string DataKey = "LA_PZ_DATA";

        /// <summary>根据比例因子计算实际生效的字体、云线等参数。若提供云线范围且开启 FontAutoFit，则按云线对角线尺寸计算字高。</summary>
        public static AnnotationSettings ResolveEffectiveSettings(Document doc, AnnotationSettings source, AnnotationData data, double cloudDiagonal = 0, bool captureRenderSettings = false)
        {
            var s=source.Clone();var ratio=Math.Max(source.ScaleRatio,0.01);var baseText=Math.Max(source.TextHeight,0.1);
            var refSize=cloudDiagonal;
            if(refSize<=0){using(var view=doc.Editor.GetCurrentView())refSize=view.Height;}
            var adaptiveText=Math.Max(0.1,refSize*Math.Max(0.1,source.AutoTextViewPercent)/100.0*ratio);
            var text=source.FontAutoFit?adaptiveText:baseText*ratio;
            var factor=text/baseText;s.TextHeight=text;s.HeaderHeight=Math.Max(0.1,source.HeaderHeight*factor);s.SecondLineHeight=Math.Max(0.1,source.SecondLineHeight*factor);
            s.FixedWidthValue=Math.Max(text*6,source.FixedWidthValue*factor);
            if(source.CloudAutoFit)
            {
                s.CloudRadius=Math.Max(0.001,adaptiveText*0.75);
                s.LineWidth=Math.Max(0.001,adaptiveText*0.035);
            }
            else
            {
                s.CloudRadius=Math.Max(0.001,source.CloudRadius*ratio);
                s.LineWidth=Math.Max(0,source.LineWidth*ratio);
            }
            s.CheckHeight=Math.Max(source.CheckHeight*factor,text*2);if(captureRenderSettings&&data!=null){data.RenderTextHeight=s.TextHeight;data.RenderHeaderHeight=s.HeaderHeight;data.RenderSecondLineHeight=s.SecondLineHeight;data.RenderCloudRadius=s.CloudRadius;data.RenderLineWidth=s.LineWidth;}return s;
        }

        private static AnnotationSettings SettingsForExisting(AnnotationData data)
        {
            var s=SettingsStore.Load();if(data.RenderTextHeight<=0)return s;s.TextHeight=data.RenderTextHeight;s.HeaderHeight=data.RenderHeaderHeight>0?data.RenderHeaderHeight:data.RenderTextHeight;s.SecondLineHeight=data.RenderSecondLineHeight>0?data.RenderSecondLineHeight:data.RenderTextHeight;s.CloudRadius=data.RenderCloudRadius>0?data.RenderCloudRadius:s.CloudRadius;s.LineWidth=data.RenderLineWidth>0?data.RenderLineWidth:s.LineWidth;return s;
        }

        /// <summary>交互式选点：第一角点 → 拖拽云线范围。文字放置由完整预览 Jig 单独完成。</summary>
        public static bool PromptGeometry(Document doc, AnnotationSettings s, out Point3d firstPoint, out Point3d secondPoint)
        {
            firstPoint=Point3d.Origin;secondPoint=Point3d.Origin;var ed = doc.Editor;object ortho=null,osmode=null;
            try{
                if(s.AutoCloseOrtho){ortho=CadSystemVariable("ORTHOMODE");SetCadSystemVariable("ORTHOMODE",0);}if(s.AutoCloseSnap){osmode=CadSystemVariable("OSMODE");SetCadSystemVariable("OSMODE",0);}
                var first = ed.GetPoint("\n指定批注范围第一个角点: "); if (first.Status != PromptStatus.OK) return false;
                var firstWcs=first.Value.TransformBy(GetUcsMatrix(doc));
                var region=new RegionPreviewJig(doc,firstWcs,s);var regionResult=ed.Drag(region);if(regionResult.Status!=PromptStatus.OK)return false;
                var (_,regionWidth,regionHeight)=UcsAlignedExtents(doc,firstWcs,region.Current);if(regionWidth<=1e-6||regionHeight<=1e-6){ed.WriteMessage("\n批注范围必须同时具有宽度和高度，请重新指定。");return false;}
                firstPoint=firstWcs;secondPoint=region.Current;return true;
            }finally{if(ortho!=null)SetCadSystemVariable("ORTHOMODE",ortho);if(osmode!=null)SetCadSystemVariable("OSMODE",osmode);}
        }

        /// <summary>交互式选点（仅云线）：第一角点 → 拖拽云线范围，不要求文字框位置。</summary>
        public static bool PromptCloudOnly(Document doc, AnnotationSettings s, out Point3d firstPoint, out Point3d secondPoint)
            => PromptCloud(doc, s, false, null, null, out firstPoint, out secondPoint) == CloudPromptResult.Completed;

        /// <summary>多对一云线选点：回车/空格结束连续绘制，Esc 取消整次操作。</summary>
        public static CloudPromptResult PromptCloudOrFinish(Document doc, AnnotationSettings s, IList<Point3d> historyFirsts, IList<Point3d> historySeconds, out Point3d firstPoint, out Point3d secondPoint)
            => PromptCloud(doc, s, true, historyFirsts, historySeconds, out firstPoint, out secondPoint);

        private static CloudPromptResult PromptCloud(Document doc, AnnotationSettings s, bool allowFinish, IList<Point3d> historyFirsts, IList<Point3d> historySeconds, out Point3d firstPoint, out Point3d secondPoint)
        {
            firstPoint=Point3d.Origin;secondPoint=Point3d.Origin;var ed=doc.Editor;object ortho=null,osmode=null;
            try{
                if(s.AutoCloseOrtho){ortho=CadSystemVariable("ORTHOMODE");SetCadSystemVariable("ORTHOMODE",0);}if(s.AutoCloseSnap){osmode=CadSystemVariable("OSMODE");SetCadSystemVariable("OSMODE",0);}
                while(true)
                {
                    Point3d firstWcs;
                    if (allowFinish)
                    {
                        var firstJig = new RegionFirstPointPreviewJig(
                            doc,
                            s,
                            historyFirsts,
                            historySeconds);
                        var firstResult = ed.Drag(firstJig);

                        if (firstJig.FinishRequested)
                        {
                            return CloudPromptResult.Finished;
                        }

                        if (firstResult.Status != PromptStatus.OK)
                        {
                            return CloudPromptResult.Cancelled;
                        }

                        firstWcs = firstJig.Current;
                    }
                    else
                    {
                        var first = ed.GetPoint("\n指定云线范围第一个角点: ");
                        if (first.Status != PromptStatus.OK)
                        {
                            return CloudPromptResult.Cancelled;
                        }

                        firstWcs = first.Value.TransformBy(GetUcsMatrix(doc));
                    }

                    var region = new RegionPreviewJig(
                        doc,
                        firstWcs,
                        s,
                        historyFirsts,
                        historySeconds);
                    var regionResult = ed.Drag(region);
                    if (regionResult.Status != PromptStatus.OK)
                    {
                        return CloudPromptResult.Cancelled;
                    }
                    var (_,width,height)=UcsAlignedExtents(doc,firstWcs,region.Current);
                    if(width<=1e-6||height<=1e-6)
                    {
                        ed.WriteMessage("\n云线范围必须同时具有宽度和高度，请重新指定。");
                        continue;
                    }
                    firstPoint=firstWcs;secondPoint=region.Current;return CloudPromptResult.Completed;
                }
            }finally{if(ortho!=null)SetCadSystemVariable("ORTHOMODE",ortho);if(osmode!=null)SetCadSystemVariable("OSMODE",osmode);}
        }

        /// <summary>仅创建云线（不含文字、引线、边框），返回实体 ObjectId。</summary>
        public static ObjectId CreateCloudOnly(Document doc, AnnotationSettings settings, Point3d firstPoint, Point3d secondPoint)
        {
            using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(doc.Database,tr);EnsureLayer(doc.Database,tr,settings);
                var space=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForWrite);
                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();var first=firstPoint.TransformBy(wcsToUcs);var second=secondPoint.TransformBy(wcsToUcs);
                var min=new Point2d(Math.Min(first.X,second.X),Math.Min(first.Y,second.Y));var max=new Point2d(Math.Max(first.X,second.X),Math.Max(first.Y,second.Y));
                var cloud=BuildCloud(min,max,settings);cloud.Elevation=first.Z;cloud.TransformBy(ucsToWcs);
                cloud.Layer=EffectiveLayer(settings);cloud.Color=Color.FromColorIndex(ColorMethod.ByAci,settings.CloudColor);
                if(settings.LineWidth>0)cloud.ConstantWidth=settings.LineWidth;
                var id=space.AppendEntity(cloud);tr.AddNewlyCreatedDBObject(cloud,true);
                tr.Commit();return id;
            }
        }

        /// <summary>仅沿用户折点创建闭合 PL 云线，与矩形批注共用多边形云线算法。</summary>
        public static ObjectId CreatePlineCloudOnly(Document doc,AnnotationSettings settings,IList<Point3d> points)
        {
            if(points==null)throw new ArgumentNullException(nameof(points));
            using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                EnsureLayer(doc.Database,tr,settings);
                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();var localPoints=points.Select(point=>point.TransformBy(wcsToUcs)).ToList();
                var cloudPoints=localPoints.Select(point=>new Point2d(point.X,point.Y)).ToList();
                var cloud=BuildPolygonCloud(cloudPoints,settings);cloud.Elevation=localPoints[0].Z;cloud.TransformBy(ucsToWcs);
                cloud.Layer=EffectiveLayer(settings);cloud.Color=Color.FromColorIndex(ColorMethod.ByAci,settings.CloudColor);
                if(settings.LineWidth>0)cloud.ConstantWidth=settings.LineWidth;
                var space=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForWrite);
                var id=space.AppendEntity(cloud);tr.AddNewlyCreatedDBObject(cloud,true);
                tr.Commit();return id;
            }
        }

        /// <summary>沿折线路径生成云线 + 文字框 + 引出线。</summary>
        public static void CreatePlineCloud(Document doc, AnnotationData data, AnnotationSettings settings, List<Point3d> points, Point3d textLocation)
        {
            using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(doc.Database,tr);EnsureLayer(doc.Database,tr,settings,data);
                var space=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForWrite);
                var ids=new ObjectIdCollection();
                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();var localPoints=points.Select(p=>p.TransformBy(wcsToUcs)).ToList();var localText=textLocation.TransformBy(wcsToUcs);
                var cloudPts=localPoints.Select(p=>new Point2d(p.X,p.Y)).ToList();
                var cloud=BuildPolygonCloud(cloudPts,settings);cloud.Elevation=localPoints[0].Z;
                // 文字 + 边框
                var margin=settings.TextHeight*0.5;
                var innerTextLocation=new Point3d(localText.X+margin,localText.Y+margin,localText.Z);
                var requestedWidth=settings.FixedWidth?settings.FixedWidthValue:Math.Max(55.0,settings.TextHeight*18.0);
                var text=new MText{Location=innerTextLocation,TextHeight=settings.TextHeight,Width=requestedWidth,Contents=FormatText(data,settings),Attachment=AttachmentPoint.BottomLeft};
                ApplyTextStyle(doc.Database,tr,text,settings.TextStyleName);
                var width=Math.Max(text.ActualWidth,settings.TextHeight*4)+margin*2;var height=Math.Max(text.ActualHeight,settings.TextHeight*2)+margin*2;
                var boxEntity=BuildBox(localText,width,height);boxEntity.Elevation=localText.Z;
                var nearestCloudPoint=cloud.GetClosestPointTo(localText,false);var nearestPt=new Point2d(nearestCloudPoint.X,nearestCloudPoint.Y);
                var boxCorners=new[]{new Point2d(localText.X,localText.Y),new Point2d(localText.X+width,localText.Y),new Point2d(localText.X+width,localText.Y+height),new Point2d(localText.X,localText.Y+height)};
                var boxCorner=boxCorners.OrderBy(c=>c.GetDistanceTo(nearestPt)).First();var leader=new Polyline();leader.AddVertexAt(0,nearestPt,0,0,0);leader.AddVertexAt(1,boxCorner,0,0,0);leader.Elevation=localText.Z;
                cloud.TransformBy(ucsToWcs);text.TransformBy(ucsToWcs);boxEntity.TransformBy(ucsToWcs);leader.TransformBy(ucsToWcs);
                Add(space,tr,cloud,ids,settings,data.Id,"cloud",settings.CloudColor,data);Add(space,tr,text,ids,settings,data.Id,"text",TextColorForStatus(settings,data.Status),data);Add(space,tr,boxEntity,ids,settings,data.Id,"box",settings.SameColors?settings.CloudColor:settings.BoxColor,data);Add(space,tr,leader,ids,settings,data.Id,"leader",settings.LeaderColor,data);
                // 编组
                var groups=(DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId,OpenMode.ForWrite);
                var group=new Group("LA批注 "+data.Number,true);
                groups.SetAt(GroupPrefix+data.Id,group);tr.AddNewlyCreatedDBObject(group,true);group.Append(ids);
                WriteMaster(group,tr,data);
                tr.Commit();
            }
        }

        /// <summary>多对一批注：清理选点阶段的临时云线，按最终参数创建云线、文字框和引出线并统一编组。</summary>
        public static void CreateMultiCloud(
            Document doc,
            AnnotationData data,
            AnnotationSettings settings,
            AnnotationSettings sourceSettings,
            List<ObjectId> previewCloudIds,
            List<Point3d> firstPoints,
            List<Point3d> secondPoints,
            Point3d textLocation)
        {
            if (firstPoints == null || secondPoints == null || firstPoints.Count == 0 || firstPoints.Count != secondPoints.Count)
                throw new ArgumentException("多对一批注的云线范围数据不完整。");
            using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(doc.Database,tr);EnsureLayer(doc.Database,tr,settings,data);
                var space=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForWrite);
                var ids=new ObjectIdCollection();
                // 临时云线只负责在连续选点期间提供可见反馈；最终使用统一的自适应参数重建并写入 XData。
                if (previewCloudIds != null)
                {
                    foreach (var previewId in previewCloudIds)
                    {
                        if (!previewId.IsValid || previewId.IsErased) continue;
                        var previewEntity = tr.GetObject(previewId, OpenMode.ForWrite, false);
                        if (previewEntity != null && !previewEntity.IsErased) previewEntity.Erase();
                    }
                }
                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();var localText=textLocation.TransformBy(wcsToUcs);var localFirsts=firstPoints.Select(p=>p.TransformBy(wcsToUcs)).ToList();var localSeconds=secondPoints.Select(p=>p.TransformBy(wcsToUcs)).ToList();
                var cloudSettings=new List<AnnotationSettings>(firstPoints.Count);var localCloudCorners=new List<Point2d[]>(firstPoints.Count);
                for(var k=0;k<localFirsts.Count;k++)
                {
                    var first=localFirsts[k];var second=localSeconds[k];var min=new Point2d(Math.Min(first.X,second.X),Math.Min(first.Y,second.Y));var max=new Point2d(Math.Max(first.X,second.X),Math.Max(first.Y,second.Y));var width=max.X-min.X;var height=max.Y-min.Y;
                    var regionSettings=ResolveEffectiveSettings(doc,sourceSettings??settings,new AnnotationData(),Math.Sqrt(width*width+height*height));cloudSettings.Add(regionSettings);
                    localCloudCorners.Add(new[]{min,new Point2d(max.X,min.Y),max,new Point2d(min.X,max.Y)});
                    var cloud=BuildCloud(min,max,regionSettings);cloud.Elevation=first.Z;cloud.TransformBy(ucsToWcs);Add(space,tr,cloud,ids,regionSettings,data.Id,"cloud",regionSettings.CloudColor,data);
                }

                var margin=settings.TextHeight*0.5;var requestedWidth=settings.FixedWidth?settings.FixedWidthValue:Math.Max(55.0,settings.TextHeight*18.0);
                var text=new MText{Location=new Point3d(localText.X+margin,localText.Y+margin,localText.Z),TextHeight=settings.TextHeight,Width=requestedWidth,Contents=FormatText(data,settings),Attachment=AttachmentPoint.BottomLeft};ApplyTextStyle(doc.Database,tr,text,settings.TextStyleName);
                var widthBox=Math.Max(text.ActualWidth,settings.TextHeight*4)+margin*2;var heightBox=Math.Max(text.ActualHeight,settings.TextHeight*2)+margin*2;var boxEntity=BuildBox(localText,widthBox,heightBox);boxEntity.Elevation=localText.Z;
                var boxCorners=new[]{new Point2d(localText.X,localText.Y),new Point2d(localText.X+widthBox,localText.Y),new Point2d(localText.X+widthBox,localText.Y+heightBox),new Point2d(localText.X,localText.Y+heightBox)};var leaders=new List<Polyline>();
                for(var k=0;k<localCloudCorners.Count;k++){var cloudCorner=localCloudCorners[k].OrderBy(c=>c.GetDistanceTo(new Point2d(localText.X,localText.Y))).First();var boxCorner=boxCorners.OrderBy(c=>c.GetDistanceTo(cloudCorner)).First();var leader=new Polyline();leader.AddVertexAt(0,cloudCorner,0,0,0);leader.AddVertexAt(1,boxCorner,0,0,0);leader.Elevation=localText.Z;leader.TransformBy(ucsToWcs);Add(space,tr,leader,ids,cloudSettings[k],data.Id,"leader",cloudSettings[k].LeaderColor,data);leaders.Add(leader);}
                text.TransformBy(ucsToWcs);boxEntity.TransformBy(ucsToWcs);Add(space,tr,text,ids,settings,data.Id,"text",TextColorForStatus(settings,data.Status),data);Add(space,tr,boxEntity,ids,settings,data.Id,"box",settings.SameColors?settings.CloudColor:settings.BoxColor,data);
                // 编组
                var groups=(DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId,OpenMode.ForWrite);
                var group=new Group("LA批注 "+data.Number,true);
                groups.SetAt(GroupPrefix+data.Id,group);tr.AddNewlyCreatedDBObject(group,true);group.Append(ids);
                WriteMaster(group,tr,data);
                tr.Commit();
            }
        }

        /// <summary>创建批注实体组：UCS 对齐云线 → 文字 → 边框 → 斜向引出线。</summary>
        public static bool Create(Document doc, AnnotationData data, AnnotationSettings settings, Point3d firstPoint, Point3d secondPoint, Point3d textLocation)
        {
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(doc.Database, tr); EnsureLayer(doc.Database, tr, settings,data);
                var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                var ids = new ObjectIdCollection();

                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();var first=firstPoint.TransformBy(wcsToUcs);var second=secondPoint.TransformBy(wcsToUcs);var localText=textLocation.TransformBy(wcsToUcs);
                var min=new Point2d(Math.Min(first.X,second.X),Math.Min(first.Y,second.Y));var max=new Point2d(Math.Max(first.X,second.X),Math.Max(first.Y,second.Y));
                var cloud=BuildCloud(min,max,settings);cloud.Elevation=first.Z;
                var requestedWidth=settings.FixedWidth?settings.FixedWidthValue:Math.Max(55.0,settings.TextHeight*18.0);var margin=settings.TextHeight*0.5;
                var text=new MText{Location=new Point3d(localText.X+margin,localText.Y+margin,localText.Z),TextHeight=settings.TextHeight,Width=requestedWidth,Contents=FormatText(data,settings),Attachment=AttachmentPoint.BottomLeft};
                ApplyTextStyle(doc.Database,tr,text,settings.TextStyleName);
                var boxW=Math.Max(text.ActualWidth,settings.TextHeight*4)+margin*2;var boxH=Math.Max(text.ActualHeight,settings.TextHeight*2)+margin*2;var boxEntity=BuildBox(localText,boxW,boxH);boxEntity.Elevation=localText.Z;
                var cloudCorner=ResolveRegionLeaderAnchor(cloud,min,max,settings,localText);var boxCorners=new[]{new Point2d(localText.X,localText.Y),new Point2d(localText.X+boxW,localText.Y),new Point2d(localText.X+boxW,localText.Y+boxH),new Point2d(localText.X,localText.Y+boxH)};var boxCorner=boxCorners.OrderBy(c=>c.GetDistanceTo(cloudCorner)).First();
                var leader=new Polyline();leader.AddVertexAt(0,cloudCorner,0,0,0);leader.AddVertexAt(1,boxCorner,0,0,0);leader.Elevation=localText.Z;
                cloud.TransformBy(ucsToWcs);text.TransformBy(ucsToWcs);boxEntity.TransformBy(ucsToWcs);leader.TransformBy(ucsToWcs);
                Add(space,tr,cloud,ids,settings,data.Id,"cloud",settings.CloudColor,data);Add(space,tr,text,ids,settings,data.Id,"text",TextColorForStatus(settings,data.Status),data);Add(space,tr,boxEntity,ids,settings,data.Id,"box",settings.SameColors?settings.CloudColor:settings.BoxColor,data);Add(space,tr,leader,ids,settings,data.Id,"leader",settings.LeaderColor,data);

                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForWrite);
                var group = new Group("LA批注 " + data.Number, true);
                groups.SetAt(GroupPrefix + data.Id, group); tr.AddNewlyCreatedDBObject(group, true); group.Append(ids);
                WriteMaster(group, tr, data);
                tr.Commit(); return true;
            }
        }

        /// <summary>从实体反向查找批注数据（通过 XData → 编组 → XRecord 链路）。</summary>
        public static bool TryReadFromEntity(Transaction tr, Entity entity, out AnnotationData data)
        {
            data = null; if (!TryGetId(entity, out var id)) return false;
            var db = entity.Database; var groups = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForRead);
            var name = GroupPrefix + id; if (!groups.Contains(name)) return false;
            var group = (Group)tr.GetObject(groups.GetAt(name), OpenMode.ForRead);
            if (!Contains(group, entity.ObjectId)) return false;
            return TryReadMaster(group, tr, out data);
        }

        /// <summary>更新批注：刷新文字内容和边框尺寸，保留编组关联。</summary>
        public static bool Update(Document doc, ObjectId entityId, AnnotationData data)
        {
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var selected = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                if (selected == null || !TryGetId(selected, out var id) || id != data.Id) return false;
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                var groupName = GroupPrefix + id; if (!groups.Contains(groupName)) return false;
                var group = (Group)tr.GetObject(groups.GetAt(groupName), OpenMode.ForWrite);
                if (!Contains(group, entityId)) return false;
                var existingSettings=SettingsForExisting(data);
                EnsureLayer(doc.Database,tr,existingSettings,data);
                var targetLayer=EffectiveLayer(existingSettings,data);
                MText changedText=null;Polyline box=null;var leaders=new List<Polyline>();
                foreach (ObjectId oid in group.GetAllEntityIds())
                {
                    if(!oid.IsValid||oid.IsErased)continue;
                    var entity = tr.GetObject(oid, OpenMode.ForWrite, false) as Entity;
                    if(entity==null)continue;
                    entity.Layer=targetLayer;
                    if (entity is MText text)
                    {
                        text.Contents=FormatText(data,existingSettings);
                        text.Color=Color.FromColorIndex(ColorMethod.ByAci,TextColorForStatus(existingSettings,data.Status));
                        changedText=text;
                    }
                    else if(entity is Polyline poly && TryGetRole(entity,out var role))
                    {
                        if(role=="box")box=poly;
                        else if(role=="leader")leaders.Add(poly);
                    }
                }
                if(changedText!=null&&box!=null&&box.NumberOfVertices>=4)
                {
                    var margin=changedText.TextHeight/2;var origin=box.GetPoint2dAt(0);var xVector=box.GetPoint2dAt(1)-origin;var yVector=box.GetPoint2dAt(3)-origin;
                    var xLength=Math.Max(xVector.Length,1e-9);var yLength=Math.Max(yVector.Length,1e-9);var xAxis=xVector/xLength;var yAxis=yVector/yLength;var width=Math.Max(changedText.ActualWidth,changedText.TextHeight*4)+margin*2;var height=Math.Max(changedText.ActualHeight,changedText.TextHeight*2)+margin*2;
                    box.SetPointAt(0,origin);
                    box.SetPointAt(1,origin+xAxis*width);
                    box.SetPointAt(2,origin+xAxis*width+yAxis*height);
                    box.SetPointAt(3,origin+yAxis*height);
                    var corners=Enumerable.Range(0,box.NumberOfVertices).Select(box.GetPoint2dAt).ToArray();
                    foreach(var leader in leaders)
                    {
                        if(leader.NumberOfVertices<2)continue;
                        var anchor=leader.GetPoint2dAt(0);leader.SetPointAt(leader.NumberOfVertices-1,corners.OrderBy(c=>c.GetDistanceTo(anchor)).First());
                    }
                }
                group.Description = "LA批注 " + data.Number; WriteMaster(group, tr, data); tr.Commit(); return true;
            }
        }

        /// <summary>移动批注的文字、文字框和引线，云线保持原位。</summary>
        public static bool MoveAnnotation(Document doc, ObjectId entityId)
        {
            var previewEntities = new List<Entity>();
            var previewLeaders = new List<Polyline>();
            Point3d basePoint;

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var selected = tr.GetObject(
                    entityId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (selected == null || !TryGetId(selected, out var id))
                {
                    return false;
                }

                var groups = (DBDictionary)tr.GetObject(
                    doc.Database.GroupDictionaryId,
                    OpenMode.ForRead);
                var groupName = GroupPrefix + id;
                if (!groups.Contains(groupName))
                {
                    return false;
                }

                var group = (Group)tr.GetObject(
                    groups.GetAt(groupName),
                    OpenMode.ForRead);
                Polyline box = null;

                foreach (ObjectId memberId in group.GetAllEntityIds())
                {
                    if (!memberId.IsValid || memberId.IsErased)
                    {
                        continue;
                    }

                    var entity = tr.GetObject(
                        memberId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (entity == null || !TryGetRole(entity, out var role))
                    {
                        continue;
                    }

                    if (role == "text" || role == "box")
                    {
                        previewEntities.Add(entity.Clone() as Entity);
                        if (role == "box")
                        {
                            box = entity as Polyline;
                        }
                    }
                    else if (role == "leader" && entity is Polyline leader)
                    {
                        previewLeaders.Add(leader.Clone() as Polyline);
                    }
                }

                if (box == null || box.NumberOfVertices == 0)
                {
                    DisposeEntities(previewEntities);
                    DisposeEntities(previewLeaders);
                    return false;
                }

                basePoint = box.GetPoint3dAt(0);
            }

            try
            {
                var jig = new MoveAnnotationPreviewJig(
                    basePoint,
                    previewEntities.Where(entity => entity != null),
                    previewLeaders.Where(leader => leader != null));
                var result = doc.Editor.Drag(jig);
                if (result.Status != PromptStatus.OK)
                {
                    return true;
                }

                var displacement = jig.Current - basePoint;
                if (displacement.Length <= 1e-8)
                {
                    return true;
                }

                ApplyAnnotationMove(doc, entityId, displacement);
                return true;
            }
            finally
            {
                DisposeEntities(previewEntities);
                DisposeEntities(previewLeaders);
            }
        }

        private static void ApplyAnnotationMove(
            Document doc,
            ObjectId entityId,
            Vector3d displacement)
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var selected = tr.GetObject(
                    entityId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (selected == null || !TryGetId(selected, out var id))
                {
                    return;
                }

                var groups = (DBDictionary)tr.GetObject(
                    doc.Database.GroupDictionaryId,
                    OpenMode.ForRead);
                var groupName = GroupPrefix + id;
                if (!groups.Contains(groupName))
                {
                    return;
                }

                var group = (Group)tr.GetObject(
                    groups.GetAt(groupName),
                    OpenMode.ForRead);
                var transform = Matrix3d.Displacement(displacement);

                foreach (ObjectId memberId in group.GetAllEntityIds())
                {
                    if (!memberId.IsValid || memberId.IsErased)
                    {
                        continue;
                    }

                    var entity = tr.GetObject(
                        memberId,
                        OpenMode.ForWrite,
                        false) as Entity;
                    if (entity == null || !TryGetRole(entity, out var role))
                    {
                        continue;
                    }

                    if (role == "text" || role == "box")
                    {
                        entity.TransformBy(transform);
                    }
                    else if (role == "leader" && entity is Polyline leader)
                    {
                        if (leader.NumberOfVertices < 2)
                        {
                            continue;
                        }

                        var anchor = leader.GetPoint2dAt(0);
                        leader.TransformBy(transform);
                        leader.SetPointAt(0, anchor);
                    }
                }

                tr.Commit();
            }
        }

        private static void DisposeEntities(IEnumerable<Entity> entities)
        {
            foreach (var entity in entities)
            {
                entity?.Dispose();
            }
        }

        /// <summary>删除整个批注编组（云线+引线+文字+边框全部擦除），支持 UNDO。</summary>
        public static bool Delete(Document doc, ObjectId entityId)
        {
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var selected = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                if (selected == null || !TryGetId(selected, out var id)) return false;
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForWrite); var name = GroupPrefix + id;
                if (!groups.Contains(name)) return false;
                var group = (Group)tr.GetObject(groups.GetAt(name), OpenMode.ForWrite);
                if (!Contains(group, entityId)) return false;
                foreach (ObjectId oid in group.GetAllEntityIds()) { if(!oid.IsValid||oid.IsErased)continue;var e = tr.GetObject(oid, OpenMode.ForWrite, false); if (e != null && !e.IsErased) e.Erase(); }
                groups.Remove(name);group.Erase();tr.Commit(); return true;
            }
        }

        /// <summary>轻量批注摘要，供列表面板展示。</summary>
        public sealed class AnnotationInfo
        {
            public string Id { get; set; }
            public string Number { get; set; }
            public string Discipline { get; set; }
            public string Author { get; set; }
            public string Role { get; set; }
            public string Date { get; set; }
            public string Status { get; set; }
            public string Content { get; set; }
            public ObjectId GroupId { get; set; }
            public ObjectId FirstEntityId { get; set; }
        }

        /// <summary>扫描当前 DWG 中现有批注编号，返回最大 LA 编号+1（无批注时=1）。</summary>
        public static int GetNextNumber(Document doc)
        {
            if (doc == null || doc.IsDisposed) return 1;
            var max = 0;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                foreach (var entry in groups)
                {
                    var name = entry.Key as string;
                    if (string.IsNullOrEmpty(name) || !name.StartsWith(GroupPrefix, StringComparison.Ordinal)) continue;
                    if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Group group)) continue;
                    if (TryReadMaster(group, tr, out var data) && !string.IsNullOrWhiteSpace(data.Number))
                    {
                        var number = data.Number.Trim();
                        var numericPart = number.StartsWith("LA-", StringComparison.OrdinalIgnoreCase) ? number.Substring(3) : number;
                        if (int.TryParse(numericPart, out var parsed) && parsed > max) max = parsed;
                    }
                }
            }
            return max + 1;
        }

        /// <summary>将本机设置中的下一个编号同步到当前 DWG。</summary>
        public static void SyncNextNumber(Document doc)
        {
            if (doc == null || doc.IsDisposed) return;
            try
            {
                var settings = SettingsStore.Load();
                settings.NextNumber = GetNextNumber(doc);
                SettingsStore.Save(settings);
            }
            catch (System.Exception ex) { PluginLog.Error("Number.Sync", ex); }
        }

        /// <summary>擦除选点阶段创建的临时实体；用于取消或失败时恢复原图状态。</summary>
        public static void EraseTemporaryEntities(Document doc, IEnumerable<ObjectId> entityIds)
        {
            if (doc == null || doc.IsDisposed || entityIds == null) return;
            try
            {
                using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    foreach(var id in entityIds)
                    {
                        if(!id.IsValid||id.IsErased)continue;
                        var entity=tr.GetObject(id,OpenMode.ForWrite,false);
                        if(entity!=null&&!entity.IsErased)entity.Erase();
                    }
                    tr.Commit();
                }
            }
            catch(System.Exception ex){PluginLog.Error("TemporaryEntities.Erase",ex);}
        }

        /// <summary>枚举当前 DWG 中所有 LA 批注。</summary>
        public static List<AnnotationInfo> GetAllAnnotations(Document doc)
        {
            var result = new List<AnnotationInfo>();
            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                    foreach (var entry in groups)
                    {
                        var name = entry.Key as string;
                        if (string.IsNullOrEmpty(name) || !name.StartsWith(GroupPrefix)) continue;
                        if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Group group)) continue;
                        if (!TryReadMaster(group, tr, out var data)) continue;
                        var ids = group.GetAllEntityIds();
                        ObjectId firstId = ObjectId.Null;
                        foreach (ObjectId oid in ids) { if (oid.IsValid && !oid.IsErased) { firstId = oid; break; } }
                        if(firstId.IsNull)continue;
                        result.Add(new AnnotationInfo
                        {
                            Id = data.Id, Number = data.Number, Discipline = data.Discipline,
                            Author = data.Author, Role = data.Role, Date = data.Date, Status = data.Status,
                            Content = data.Content, GroupId = group.ObjectId, FirstEntityId = firstId
                        });
                    }
                }
            }
            catch { }
            return result.OrderBy(x => x.Number, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>缩放视图到批注实体范围。</summary>
        public static void ZoomToAnnotation(Document doc, ObjectId entityId)
        {
            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                    if (entity == null) return;
                    var ext = entity.GeometricExtents;
                    // 若该实体属于某个编组，扩展到编组中全部实体
                    if (TryGetId(entity, out var id))
                    {
                        var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                        var groupName = GroupPrefix + id;
                        if (groups.Contains(groupName))
                        {
                            var group = (Group)tr.GetObject(groups.GetAt(groupName), OpenMode.ForRead);
                            foreach (ObjectId oid in group.GetAllEntityIds())
                            {
                                if (!oid.IsValid || oid.IsErased) continue;
                                var e = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                                if (e != null) { ext.AddPoint(e.GeometricExtents.MinPoint); ext.AddPoint(e.GeometricExtents.MaxPoint); }
                            }
                        }
                    }
                    // 实体范围是 WCS；视图 CenterPoint/Width/Height 使用 DCS，必须先转换。
                    using (var view = doc.Editor.GetCurrentView())
                    {
                        var wcsToDcs=Matrix3d.PlaneToWorld(view.ViewDirection);
                        wcsToDcs=Matrix3d.Displacement(view.Target-Point3d.Origin)*wcsToDcs;
                        wcsToDcs=Matrix3d.Rotation(-view.ViewTwist,view.ViewDirection,view.Target)*wcsToDcs;
                        wcsToDcs=wcsToDcs.Inverse();
                        var min=ext.MinPoint;var max=ext.MaxPoint;
                        var dcsCorners=new[]{
                            new Point3d(min.X,min.Y,min.Z),new Point3d(max.X,min.Y,min.Z),new Point3d(max.X,max.Y,min.Z),new Point3d(min.X,max.Y,min.Z),
                            new Point3d(min.X,min.Y,max.Z),new Point3d(max.X,min.Y,max.Z),new Point3d(max.X,max.Y,max.Z),new Point3d(min.X,max.Y,max.Z)
                        }.Select(point=>point.TransformBy(wcsToDcs)).ToArray();
                        var minX=dcsCorners.Min(point=>point.X);var maxX=dcsCorners.Max(point=>point.X);var minY=dcsCorners.Min(point=>point.Y);var maxY=dcsCorners.Max(point=>point.Y);
                        var width=Math.Max(maxX-minX,1);var height=Math.Max(maxY-minY,1);var margin=1.2;
                        view.CenterPoint=new Point2d((minX+maxX)/2,(minY+maxY)/2);
                        view.Width=width*margin;view.Height=height*margin;
                        doc.Editor.SetCurrentView(view);
                    }
                }
            }
            catch { }
        }

        private static void WriteMaster(Group group, Transaction tr, AnnotationData data)
        {
            if (group.ExtensionDictionary.IsNull) group.CreateExtensionDictionary();
            var dict = (DBDictionary)tr.GetObject(group.ExtensionDictionary, OpenMode.ForWrite);
            Xrecord record;
            if (dict.Contains(DataKey)) record = (Xrecord)tr.GetObject(dict.GetAt(DataKey), OpenMode.ForWrite);
            else { record = new Xrecord(); dict.SetAt(DataKey, record); tr.AddNewlyCreatedDBObject(record, true); }
            var values = AnnotationCodec.Split(AnnotationCodec.Encode(data)).Select(x => new TypedValue((int)DxfCode.Text, x)).ToArray();
            record.Data = new ResultBuffer(values);
        }

        private static bool TryReadMaster(Group group, Transaction tr, out AnnotationData data)
        {
            data = null; if (group.ExtensionDictionary.IsNull) return false;
            var dict = (DBDictionary)tr.GetObject(group.ExtensionDictionary, OpenMode.ForRead); if (!dict.Contains(DataKey)) return false;
            var record = (Xrecord)tr.GetObject(dict.GetAt(DataKey), OpenMode.ForRead); if (record.Data == null) return false;
            var value = string.Concat(record.Data.AsArray().Where(v => v.TypeCode == (int)DxfCode.Text).Select(v => v.Value as string));
            return AnnotationCodec.TryDecode(value, out data);
        }

        private static bool TryGetId(Entity entity, out string id)
        {
            id = null; var rb = entity.GetXDataForApplication(AnnotationCodec.AppName); if (rb == null) return false;
            var values = rb.AsArray(); if (values.Length < 2) return false; id = values[1].Value as string; return !string.IsNullOrWhiteSpace(id);
        }

        internal static AnnotationSettings SettingsForRegion(Document doc,AnnotationSettings source,Point3d first,Point3d second){var (_,w,h)=UcsAlignedExtents(doc,first,second);return ResolveEffectiveSettings(doc,source,new AnnotationData(),Math.Sqrt(w*w+h*h));}
        internal static void ApplyPreviewAppearance(Entity entity,AnnotationSettings settings,short color,string role){entity.Color=Color.FromColorIndex(ColorMethod.ByAci,color);if(entity is Polyline poly&&settings.LineWidth>0&&(role=="cloud"||role=="leader"))poly.ConstantWidth=settings.LineWidth;}
        internal enum InteractionStatus { Accepted, Cancelled, Failed }
        internal readonly struct InteractionResult
        {
            public InteractionStatus Status { get; }
            public PromptStatus PromptStatus { get; }
            public string Stage { get; }
            public Point3d Point { get; }
            public bool FinishRequested { get; }

            private InteractionResult(
                InteractionStatus status,
                PromptStatus promptStatus,
                string stage,
                Point3d point,
                bool finishRequested = false)
            {
                Status = status;
                PromptStatus = promptStatus;
                Stage = stage;
                Point = point;
                FinishRequested = finishRequested;
            }

            public static InteractionResult From(
                PromptResult result,
                string stage,
                Point3d point,
                bool finishRequested = false)
            {
                var status = result.Status == PromptStatus.OK
                    ? InteractionStatus.Accepted
                    : result.Status == PromptStatus.Cancel ||
                      result.Status == PromptStatus.None
                        ? InteractionStatus.Cancelled
                        : InteractionStatus.Failed;
                return new InteractionResult(
                    status,
                    result.Status,
                    stage,
                    point,
                    finishRequested);
            }
        }

        internal static InteractionResult PromptPlacement(
            Document doc,
            AnnotationSettings settings,
            AnnotationSettings source,
            IList<Point3d> firsts,
            IList<Point3d> seconds,
            IList<Point3d> polygon,
            Point3d initial,
            PlacementGeometryKind kind)
        {
            var jig = new PlacementPreviewJig(
                doc,
                settings,
                source,
                firsts,
                seconds,
                polygon,
                initial,
                kind);
            var result = doc.Editor.Drag(jig);
            return InteractionResult.From(
                result,
                "批注框定位",
                jig.Current);
        }
        internal static InteractionResult PromptPlinePoint(
            Document doc,
            IList<Point3d> points,
            AnnotationSettings settings)
        {
            var jig = new PlinePointPreviewJig(doc, points, settings);
            var result = doc.Editor.Drag(jig);
            return InteractionResult.From(
                result,
                "PL 点选择",
                jig.Current,
                jig.FinishRequested);
        }


        internal static string FormatText(AnnotationData d,AnnotationSettings s) => $"\\H{s.HeaderHeight:0.###};{Escape(d.Number)}    {Escape(d.Discipline)}    {Escape(d.Author)}（{Escape(d.Role)}）    {Escape(d.Date)}\\P\\H{s.TextHeight:0.###};{Escape(d.Content).Replace("\r\n", "\\P").Replace("\n", "\\P")}\\P\\H{s.SecondLineHeight:0.###};状态: {Escape(d.Status)}";
        private static string Escape(string value) => (value ?? "").Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
        private static short TextColorForStatus(AnnotationSettings settings,string status)
        {
            if(settings.SameColors)return settings.ColorIndex;
            if(string.Equals(status,"已完成",StringComparison.OrdinalIgnoreCase))return settings.PassColor;
            if(string.Equals(status,"已回复",StringComparison.OrdinalIgnoreCase))return settings.ReplyColor;
            return settings.TextColor;
        }
        internal static Polyline BuildBox(Point3d p, double w, double h) { var x=new Polyline();x.AddVertexAt(0,new Point2d(p.X,p.Y),0,0,0);x.AddVertexAt(1,new Point2d(p.X+w,p.Y),0,0,0);x.AddVertexAt(2,new Point2d(p.X+w,p.Y+h),0,0,0);x.AddVertexAt(3,new Point2d(p.X,p.Y+h),0,0,0);x.Closed=true;return x; }
        /// <summary>构建云线多段线，支持矩形/菱形/椭圆三种外形，开启 CloudAutoFit 时自适应弧段数。</summary>
        internal static Polyline BuildCloud(Point2d min, Point2d max, AnnotationSettings settings)
        {
            var radius=Math.Max(settings.CloudRadius,0.001);
            var w=Math.Max(0,max.X-min.X);var h=Math.Max(0,max.Y-min.Y);
            var perimeter=ComputePerimeter(w,h,settings.Shape);
            if(perimeter<1e-9){var empty=new Polyline();empty.AddVertexAt(0,min,0,0,0);empty.AddVertexAt(1,max,0,0,0);return empty;}
            var spacing=Math.Max(0.1,radius*2);
            if(settings.CloudAutoFit){var desired=Math.Max(20,Math.Min(160,(int)Math.Ceiling(perimeter/Math.Max(settings.TextHeight*1.2,0.1))));spacing=perimeter/desired;}
            // 菱形：使用框选云线专用的四边细分，不进入 PL 多边形校验。
            if(settings.Shape=="菱形"){var cx=(min.X+max.X)/2;var cy=(min.Y+max.Y)/2;return BuildRegionCloudPath(new[]{new Point2d(cx,min.Y),new Point2d(max.X,cy),new Point2d(cx,max.Y),new Point2d(min.X,cy)},spacing,settings.CloudStyle);}
            // 椭圆：自适应采样点数
            if(settings.Shape=="椭圆"){var cx=(min.X+max.X)/2;var cy=(min.Y+max.Y)/2;var rx=w/2;var ry=h/2;var count=Math.Max(20,Math.Min(160,(int)Math.Ceiling(perimeter/spacing)));var points=new List<Point2d>();for(var i=0;i<count;i++){var a=2*Math.PI*i/count;points.Add(new Point2d(cx+rx*Math.Cos(a),cy+ry*Math.Sin(a)));}return BuildScallopedVertices(points,settings.CloudStyle);}
            // 矩形：按宽高分别均匀布点，与任意折点的 PL 云线算法完全分离。
            var nx=Math.Max(4,(int)Math.Ceiling(w/spacing));var ny=Math.Max(4,(int)Math.Ceiling(h/spacing));
            const int maxVertices=400;var total=2*(nx+ny);if(total>maxVertices){var scale=(double)maxVertices/total;nx=Math.Max(4,(int)Math.Floor(nx*scale));ny=Math.Max(4,(int)Math.Floor(ny*scale));}
            var rectanglePoints=new List<Point2d>(2*(nx+ny));
            for(var i=0;i<nx;i++)rectanglePoints.Add(new Point2d(min.X+w*i/nx,min.Y));
            for(var i=0;i<ny;i++)rectanglePoints.Add(new Point2d(max.X,min.Y+h*i/ny));
            for(var i=0;i<nx;i++)rectanglePoints.Add(new Point2d(max.X-w*i/nx,max.Y));
            for(var i=0;i<ny;i++)rectanglePoints.Add(new Point2d(min.X,max.Y-h*i/ny));
            return BuildScallopedVertices(rectanglePoints,settings.CloudStyle);
        }
        /// <summary>计算各外形的近似周长，用于自适应弧段数。</summary>
        private static double ComputePerimeter(double w, double h, string shape)
        {
            if(shape=="菱形") return 2*Math.Sqrt(w*w+h*h);        // 菱形周长 = 2√(w²+h²)
            if(shape=="椭圆") return Math.PI*(w+h)/2*1.05;        // 椭圆近似周长
            return 2*(w+h);                                        // 矩形周长
        }
        /// <summary>按当前 UCS 对齐的四个 WCS 角点构建框选云线。</summary>
        internal static Polyline BuildRegionCloud(Point2d[] corners,AnnotationSettings settings)
        {
            if(settings.Shape=="菱形")
            {
                var diamond=new[]{MidPoint(corners[0],corners[1]),MidPoint(corners[1],corners[2]),MidPoint(corners[2],corners[3]),MidPoint(corners[3],corners[0])};
                return BuildCloudFromUcsCorners(diamond,settings);
            }
            if(settings.Shape=="椭圆")
            {
                var center=new Point2d((corners[0].X+corners[2].X)/2,(corners[0].Y+corners[2].Y)/2);
                var axisX=new Vector2d((corners[1].X-corners[0].X)/2,(corners[1].Y-corners[0].Y)/2);
                var axisY=new Vector2d((corners[3].X-corners[0].X)/2,(corners[3].Y-corners[0].Y)/2);
                var perimeter=Math.PI*(axisX.Length+axisY.Length)*1.05;var spacing=Math.Max(settings.CloudRadius*2,0.1);
                if(settings.CloudAutoFit){var desired=Math.Max(20,Math.Min(160,(int)Math.Ceiling(perimeter/Math.Max(settings.TextHeight*1.2,0.1))));spacing=perimeter/desired;}
                var count=Math.Max(20,Math.Min(160,(int)Math.Ceiling(perimeter/spacing)));var points=new List<Point2d>(count);
                for(var i=0;i<count;i++){var angle=2*Math.PI*i/count;points.Add(center+axisX*Math.Cos(angle)+axisY*Math.Sin(angle));}
                return BuildScallopedVertices(points,settings.CloudStyle);
            }
            return BuildCloudFromUcsCorners(corners,settings);
        }

        private static Point2d MidPoint(Point2d a,Point2d b)=>new Point2d((a.X+b.X)/2,(a.Y+b.Y)/2);

        /// <summary>用四个角点（WCS）构建 UCS 对齐云线（仅矩形外形）。</summary>
        internal static Polyline BuildCloudFromUcsCorners(Point2d[] wcsCorners,AnnotationSettings settings)
        {
            var spacing=Math.Max(settings.CloudRadius*2,0.1);
            var perimeter=0.0;for(var i=0;i<wcsCorners.Length;i++)perimeter+=wcsCorners[i].GetDistanceTo(wcsCorners[(i+1)%wcsCorners.Length]);
            if(settings.CloudAutoFit){var desired=Math.Max(20,Math.Min(160,(int)Math.Ceiling(perimeter/Math.Max(settings.TextHeight*1.2,0.1))));spacing=perimeter/desired;}
            return BuildRegionCloudPath(wcsCorners,spacing,settings.CloudStyle);
        }

        /// <summary>框选区域专用云线路径细分；矩形和菱形不经过 PL 折点校验。</summary>
        private static Polyline BuildRegionCloudPath(IList<Point2d> corners,double spacing,string style)
        {
            var points=new List<Point2d>();
            for(var i=0;i<corners.Count;i++)
            {
                var a=corners[i];var b=corners[(i+1)%corners.Count];
                var count=Math.Min(100,Math.Max(4,(int)Math.Ceiling(a.GetDistanceTo(b)/Math.Max(spacing,0.1))));
                for(var j=0;j<count;j++)points.Add(new Point2d(a.X+(b.X-a.X)*j/count,a.Y+(b.Y-a.Y)*j/count));
            }
            return BuildScallopedVertices(points,style);
        }

        internal static Point2d ResolveRegionLeaderAnchor(
            Polyline cloud,
            Point2d min,
            Point2d max,
            AnnotationSettings settings,
            Point3d target)
        {
            if (settings.Shape == "椭圆")
            {
                var closest = cloud.GetClosestPointTo(target, false);
                return new Point2d(closest.X, closest.Y);
            }

            return ClosestCorner(min, max, target);
        }

        internal static Point2d ClosestCorner(Point2d min,Point2d max,Point3d p){var target=new Point2d(p.X,p.Y);var a=new[]{min,new Point2d(max.X,min.Y),max,new Point2d(min.X,max.Y)};return a.OrderBy(x=>x.GetDistanceTo(target)).First();}
        /// <summary>给定 4 个 WCS 角点，找离目标最近的角。</summary>
        internal static Point2d ClosestCorner4(Point2d[] corners,Point3d p){var target=new Point2d(p.X,p.Y);return corners.OrderBy(c=>c.GetDistanceTo(target)).First();}
        private static bool Contains(Group group,ObjectId id){foreach(ObjectId member in group.GetAllEntityIds())if(member==id)return true;return false;}
        private static void Add(BlockTableRecord space,Transaction tr,Entity e,ObjectIdCollection ids,AnnotationSettings s,string id,string role,short color,AnnotationData data=null){e.Layer=EffectiveLayer(s,data);e.Color=Color.FromColorIndex(ColorMethod.ByAci,color);if(e is Polyline p&&s.LineWidth>0&&(role=="cloud"||role=="leader"))p.ConstantWidth=s.LineWidth;e.XData=new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName,AnnotationCodec.AppName),new TypedValue((int)DxfCode.ExtendedDataAsciiString,id),new TypedValue((int)DxfCode.ExtendedDataAsciiString,role));ids.Add(space.AppendEntity(e));tr.AddNewlyCreatedDBObject(e,true);}
        private static bool TryGetRole(Entity e,out string role){role=null;var rb=e.GetXDataForApplication(AnnotationCodec.AppName);if(rb==null)return false;var a=rb.AsArray();if(a.Length<3)return false;role=a[2].Value as string;return role!=null;}
        /// <summary>矩形、菱形、UCS 矩形和 PL 线共用的闭合多边形云线算法。</summary>
        internal static Polyline BuildPolygonCloud(IList<Point2d> sourcePoints,AnnotationSettings settings)
        {
            if(!TryNormalizePolygon(sourcePoints,out var corners,out var error))throw new ArgumentException(error);
            var lengths=new double[corners.Count];var perimeter=0.0;
            for(var i=0;i<corners.Count;i++){lengths[i]=corners[i].GetDistanceTo(corners[(i+1)%corners.Count]);perimeter+=lengths[i];}

            var spacing=Math.Max(settings.CloudRadius*2,0.1);
            if(settings.CloudAutoFit)
            {
                var desired=Math.Max(20,Math.Min(160,(int)Math.Ceiling(perimeter/Math.Max(settings.TextHeight*1.2,0.1))));
                spacing=perimeter/desired;
            }

            var minimumPerEdge=corners.Count<=4?4:1;
            var segmentCounts=lengths.Select(length=>Math.Max(minimumPerEdge,(int)Math.Ceiling(length/spacing))).ToArray();
            const int maxVertices=400;
            var total=segmentCounts.Sum();
            if(total>maxVertices)
            {
                var scale=(double)maxVertices/total;
                for(var i=0;i<segmentCounts.Length;i++)segmentCounts[i]=Math.Max(1,(int)Math.Floor(segmentCounts[i]*scale));
                while(segmentCounts.Sum()>maxVertices)
                {
                    var index=Array.IndexOf(segmentCounts,segmentCounts.Max());
                    if(segmentCounts[index]<=1)break;
                    segmentCounts[index]--;
                }
            }

            var sampled=new List<Point2d>(Math.Min(maxVertices,segmentCounts.Sum()));
            for(var i=0;i<corners.Count;i++)
            {
                var a=corners[i];var b=corners[(i+1)%corners.Count];var count=segmentCounts[i];
                for(var j=0;j<count;j++)sampled.Add(new Point2d(a.X+(b.X-a.X)*j/count,a.Y+(b.Y-a.Y)*j/count));
            }
            return BuildScallopedVertices(sampled,settings.CloudStyle);
        }

        /// <summary>去除重复点并统一为逆时针方向，使负 bulge 始终向多边形外侧鼓出。</summary>
        private static bool TryNormalizePolygon(IList<Point2d> sourcePoints,out List<Point2d> points,out string error)
        {
            points=new List<Point2d>();error=null;
            if(sourcePoints!=null)
            {
                foreach(var point in sourcePoints)
                {
                    if(points.Count==0||points[points.Count-1].GetDistanceTo(point)>1e-8)points.Add(point);
                }
            }
            if(points.Count>1&&points[0].GetDistanceTo(points[points.Count-1])<=1e-8)points.RemoveAt(points.Count-1);
            if(points.Count<3){error="PL 云线至少需要三个不同的点。";return false;}
            if(points.Count>400){error="PL 云线原始折点不能超过 400 个。";return false;}
            for(var i=0;i<points.Count;i++)
            {
                for(var j=i+1;j<points.Count;j++)
                {
                    if(points[i].GetDistanceTo(points[j])<=1e-8){error="PL 云线不能重复经过同一个折点。";return false;}
                }
            }
            if(HasSelfIntersection(points)){error="PL 云线边界不能自相交。";return false;}

            var signedArea=0.0;
            for(var i=0;i<points.Count;i++){var a=points[i];var b=points[(i+1)%points.Count];signedArea+=a.X*b.Y-b.X*a.Y;}
            var minX=points.Min(p=>p.X);var maxX=points.Max(p=>p.X);var minY=points.Min(p=>p.Y);var maxY=points.Max(p=>p.Y);
            var areaTolerance=Math.Max(1e-10,(maxX-minX)*(maxY-minY)*1e-10);
            if(Math.Abs(signedArea)*0.5<=areaTolerance){error="PL 云线的点不能全部共线或形成零面积区域。";return false;}
            if(signedArea<0)points.Reverse();
            return true;
        }

        private static bool HasSelfIntersection(IList<Point2d> points)
        {
            for(var i=0;i<points.Count;i++)
            {
                var a1=points[i];var a2=points[(i+1)%points.Count];
                for(var j=i+1;j<points.Count;j++)
                {
                    if(j==i||j==(i+1)%points.Count||(j+1)%points.Count==i)continue;
                    var b1=points[j];var b2=points[(j+1)%points.Count];
                    if(SegmentsIntersect(a1,a2,b1,b2))return true;
                }
            }
            return false;
        }

        private static bool SegmentsIntersect(Point2d a,Point2d b,Point2d c,Point2d d)
        {
            var abC=Cross(a,b,c);var abD=Cross(a,b,d);var cdA=Cross(c,d,a);var cdB=Cross(c,d,b);
            const double tolerance=1e-10;
            if(((abC>tolerance&&abD<-tolerance)||(abC<-tolerance&&abD>tolerance))&&
               ((cdA>tolerance&&cdB<-tolerance)||(cdA<-tolerance&&cdB>tolerance)))return true;
            return Math.Abs(abC)<=tolerance&&OnSegment(a,b,c)||Math.Abs(abD)<=tolerance&&OnSegment(a,b,d)||
                   Math.Abs(cdA)<=tolerance&&OnSegment(c,d,a)||Math.Abs(cdB)<=tolerance&&OnSegment(c,d,b);
        }

        private static double Cross(Point2d a,Point2d b,Point2d p)=>(b.X-a.X)*(p.Y-a.Y)-(b.Y-a.Y)*(p.X-a.X);
        private static bool OnSegment(Point2d a,Point2d b,Point2d p)=>p.X>=Math.Min(a.X,b.X)-1e-10&&p.X<=Math.Max(a.X,b.X)+1e-10&&p.Y>=Math.Min(a.Y,b.Y)-1e-10&&p.Y<=Math.Max(a.Y,b.Y)+1e-10;

        internal static bool ValidateCloudPolygon(IList<Point2d> points,out string error)
        {
            return TryNormalizePolygon(points,out var ignored,out error);
        }
        /// <summary>沿顶点序列生成锯齿云线，始终闭合。</summary>
        internal static Polyline BuildScallopedVertices(IList<Point2d> points,string style){var p=new Polyline();for(var i=0;i<points.Count;i++)p.AddVertexAt(i,points[i],style=="等宽"?-0.55:(i%2==0?-0.35:-0.7),0,0);p.Closed=true;return p;}
        internal static string EffectiveLayer(AnnotationSettings s,AnnotationData data=null)
        {
            var date=DateTime.Today;
            if(data!=null&&!string.IsNullOrWhiteSpace(data.Date))
            {
                if(DateTime.TryParseExact(data.Date,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var parsedDate))date=parsedDate;
            }
            var author=data!=null&&!string.IsNullOrWhiteSpace(data.Author)?data.Author:s.DefaultAuthor;
            var parts=new List<string>{s.LayerName};
            if(s.LayerAppendDate&&s.DateBeforeName)parts.Add(date.ToString("yyyyMMdd"));
            if(s.LayerAppendName)parts.Add(author);
            if(s.LayerAppendDate&&!s.DateBeforeName)parts.Add(date.ToString("yyyyMMdd"));
            var connector=s.Connector=="无"?"":s.Connector;
            return string.Join(connector,parts.Where(x=>!string.IsNullOrWhiteSpace(x)));
        }
        /// <summary>应用文字样式；若指定样式不存在则记录警告并回退为默认样式。</summary>
        private static void ApplyTextStyle(Database db,Transaction tr,MText text,string name)
        {
            if(string.IsNullOrWhiteSpace(name))return;
            var table=(TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead);
            if(table.Has(name)){text.TextStyleId=table[name];}
            else PluginLog.Warning("TextStyle",$"文字样式「{name}」在图中不存在，已回退为默认样式。");
        }
#if ZWCAD
        private static object CadSystemVariable(string name)=>ZwSoft.ZwCAD.ApplicationServices.Core.Application.GetSystemVariable(name);
        private static void SetCadSystemVariable(string name,object value)=>ZwSoft.ZwCAD.ApplicationServices.Core.Application.SetSystemVariable(name,value);
#else
        private static object CadSystemVariable(string name)=>Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable(name);
        private static void SetCadSystemVariable(string name,object value)=>Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable(name,value);
#endif

        /// <summary>获取当前 UCS → WCS 变换矩阵。</summary>
        internal static Matrix3d GetUcsMatrix(Document doc){try{return doc.Editor.CurrentUserCoordinateSystem;}catch{return Matrix3d.Identity;}}
        /// <summary>WCS 点 → UCS 点。</summary>
        internal static Point3d WcsToUcs(Document doc,Point3d wcsPt){try{return wcsPt.TransformBy(GetUcsMatrix(doc).Inverse());}catch{return wcsPt;}}
        /// <summary>UCS 点 → WCS 点。</summary>
        internal static Point3d UcsToWcs(Document doc,Point3d ucsPt){try{return ucsPt.TransformBy(GetUcsMatrix(doc));}catch{return ucsPt;}}
        /// <summary>将 WCS 两点转为 UCS 对齐矩形（min, width, height）。</summary>
        internal static (Point3d origin,double w,double h) UcsAlignedExtents(Document doc,Point3d a,Point3d b){var ua=WcsToUcs(doc,a);var ub=WcsToUcs(doc,b);return (new Point3d(Math.Min(ua.X,ub.X),Math.Min(ua.Y,ub.Y),ua.Z),Math.Abs(ub.X-ua.X),Math.Abs(ub.Y-ua.Y));}

        private static void EnsureRegApp(Database db,Transaction tr){var t=(RegAppTable)tr.GetObject(db.RegAppTableId,OpenMode.ForRead);if(t.Has(AnnotationCodec.AppName))return;t.UpgradeOpen();var r=new RegAppTableRecord{Name=AnnotationCodec.AppName};t.Add(r);tr.AddNewlyCreatedDBObject(r,true);}
        private static void EnsureLayer(Database db,Transaction tr,AnnotationSettings s,AnnotationData data=null)
        {
            var name=EffectiveLayer(s,data);var t=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);
            if(t.Has(name))
            {
                var existing=(LayerTableRecord)tr.GetObject(t[name],OpenMode.ForWrite);
                existing.IsPlottable=s.Plottable;
                return;
            }
            t.UpgradeOpen();var r=new LayerTableRecord{Name=name,Color=Color.FromColorIndex(ColorMethod.ByAci,s.CloudColor),IsPlottable=s.Plottable};t.Add(r);tr.AddNewlyCreatedDBObject(r,true);
        }
    }
}
