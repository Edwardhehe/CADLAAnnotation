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
    /// <summary>批注核心服务：几何交互、实体创建/读写/更新/删除。</summary>
    internal static class AnnotationService
    {
        /// <summary>CAD 编组名前缀，用于关联批注的所有子实体。</summary>
        private const string GroupPrefix = "LA_PZ_NOTE_";
        /// <summary>XRecord 数据键名，用于在编组扩展字典中存储批注数据。</summary>
        private const string DataKey = "LA_PZ_DATA";

        /// <summary>根据当前视图和比例因子计算实际生效的字体、云线等参数。</summary>
        public static AnnotationSettings ResolveEffectiveSettings(Document doc, AnnotationSettings source, AnnotationData data)
        {
            var s=source.Clone();var ratio=Math.Max(source.ScaleRatio,0.01);var baseText=Math.Max(source.TextHeight,0.1);var text=baseText*ratio;
            if(source.FontAutoFit)
            {
                using(var view=doc.Editor.GetCurrentView()) text=Math.Max(text,view.Height*Math.Max(0.1,source.AutoTextViewPercent)/100.0);
            }
            var factor=text/baseText;s.TextHeight=text;s.HeaderHeight=Math.Max(0.1,source.HeaderHeight*factor);s.SecondLineHeight=Math.Max(0.1,source.SecondLineHeight*factor);
            s.FixedWidthValue=Math.Max(text*6,source.FixedWidthValue*factor);s.LineWidth=Math.Max(source.LineWidth*ratio,text*0.035);
            s.CloudRadius=source.CloudAutoFit?Math.Max(source.CloudRadius*ratio,text*0.75):source.CloudRadius*ratio;s.CheckHeight=Math.Max(source.CheckHeight*factor,text*2);
            data.RenderTextHeight=s.TextHeight;data.RenderHeaderHeight=s.HeaderHeight;data.RenderSecondLineHeight=s.SecondLineHeight;data.RenderCloudRadius=s.CloudRadius;data.RenderLineWidth=s.LineWidth;return s;
        }

        private static AnnotationSettings SettingsForExisting(AnnotationData data)
        {
            var s=SettingsStore.Load();if(data.RenderTextHeight<=0)return s;s.TextHeight=data.RenderTextHeight;s.HeaderHeight=data.RenderHeaderHeight>0?data.RenderHeaderHeight:data.RenderTextHeight;s.SecondLineHeight=data.RenderSecondLineHeight>0?data.RenderSecondLineHeight:data.RenderTextHeight;s.CloudRadius=data.RenderCloudRadius>0?data.RenderCloudRadius:s.CloudRadius;s.LineWidth=data.RenderLineWidth>0?data.RenderLineWidth:s.LineWidth;return s;
        }

        /// <summary>交互式选点：第一角点 → 拖拽云线范围 → 拖拽文字框位置。可选自动关闭正交/捕捉。</summary>
        public static bool PromptGeometry(Document doc, AnnotationSettings s, out Point3d firstPoint, out Point3d secondPoint, out Point3d textLocation)
        {
            firstPoint=Point3d.Origin;secondPoint=Point3d.Origin;textLocation=Point3d.Origin;var ed = doc.Editor;object ortho=null,osmode=null;
            try{
                if(s.AutoCloseOrtho){ortho=CadSystemVariable("ORTHOMODE");SetCadSystemVariable("ORTHOMODE",0);}if(s.AutoCloseSnap){osmode=CadSystemVariable("OSMODE");SetCadSystemVariable("OSMODE",0);}
                var first = ed.GetPoint("\n指定批注范围第一个角点: "); if (first.Status != PromptStatus.OK) return false;
                var region=new RegionPreviewJig(first.Value,s);var regionResult=ed.Drag(region);if(regionResult.Status!=PromptStatus.OK)return false;
                if(first.Value.DistanceTo(region.Current)<1e-6){ed.WriteMessage("\n批注范围太小，请重新指定两个不同的角点。");return false;}
                var placement=new PlacementPreviewJig(first.Value,region.Current,s);var placementResult=ed.Drag(placement);if(placementResult.Status!=PromptStatus.OK)return false;
                firstPoint=first.Value;secondPoint=region.Current;textLocation=placement.Current;return true;
            }finally{if(ortho!=null)SetCadSystemVariable("ORTHOMODE",ortho);if(osmode!=null)SetCadSystemVariable("OSMODE",osmode);}
        }

        /// <summary>创建批注实体组：云线 + L 形引线 + 多行文字 + 矩形边框，统一编组并写入 XRecord 数据。</summary>
        public static bool Create(Document doc, AnnotationData data, AnnotationSettings settings, Point3d firstPoint, Point3d secondPoint, Point3d textLocation)
        {
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(doc.Database, tr); EnsureLayer(doc.Database, tr, settings);
                var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                var ids = new ObjectIdCollection();
                var min = new Point2d(Math.Min(firstPoint.X, secondPoint.X), Math.Min(firstPoint.Y, secondPoint.Y));
                var max = new Point2d(Math.Max(firstPoint.X, secondPoint.X), Math.Max(firstPoint.Y, secondPoint.Y));
                var cloud=BuildCloud(min, max, settings);cloud.Elevation=firstPoint.Z;Add(space, tr, cloud, ids, settings, data.Id, "cloud", settings.CloudColor);

                var attach = ClosestCorner(min, max, textLocation);
                var leader = new Polyline(); leader.AddVertexAt(0, attach, 0, 0, 0);
                leader.AddVertexAt(1, new Point2d(textLocation.X, attach.Y), 0, 0, 0);
                leader.AddVertexAt(2, new Point2d(textLocation.X, textLocation.Y), 0, 0, 0);
                leader.Elevation=textLocation.Z;
                Add(space, tr, leader, ids, settings, data.Id, "leader", settings.LeaderColor);

                var requestedWidth = settings.FixedWidth ? settings.FixedWidthValue : Math.Max(55.0, settings.TextHeight * 18.0);
                var margin=settings.TextHeight*0.5;var innerTextLocation=new Point3d(textLocation.X+margin,textLocation.Y+margin,textLocation.Z);
                var text = new MText { Location = innerTextLocation, TextHeight = settings.TextHeight, Width = requestedWidth, Contents = FormatText(data,settings), Attachment = AttachmentPoint.BottomLeft };
                ApplyTextStyle(doc.Database,tr,text,settings.TextStyleName);
                Add(space, tr, text, ids, settings, data.Id, "text", settings.TextColor);
                var width=Math.Max(text.ActualWidth,settings.TextHeight*4)+margin*2;var height=Math.Max(text.ActualHeight,settings.TextHeight*2)+margin*2;
                var boxEntity=BuildBox(textLocation, width, height);boxEntity.Elevation=textLocation.Z;Add(space, tr, boxEntity, ids, settings, data.Id, "box", settings.SameColors?settings.CloudColor:settings.BoxColor);

                // 视图旋转补偿：若用户指定视图上方非北，则按当前视图扭转角旋转文字和边框
                if (!settings.ViewTopIsNorth) RotateAnnotationByViewTwist(text, boxEntity, textLocation);

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
                MText changedText=null;Polyline box=null;
                foreach (ObjectId oid in group.GetAllEntityIds())
                {
                    if(!oid.IsValid||oid.IsErased)continue;
                    var entity = tr.GetObject(oid, OpenMode.ForWrite, false) as Entity;
                    if (entity is MText text){text.Contents = FormatText(data,SettingsForExisting(data));changedText=text;}
                    else if(entity is Polyline poly && TryGetRole(entity,out var role) && role=="box")box=poly;
                }
                if(changedText!=null&&box!=null){var margin=changedText.TextHeight/2;var origin=new Point3d(changedText.Location.X-margin,changedText.Location.Y-margin,changedText.Location.Z);ResizeBox(box,origin,Math.Max(changedText.ActualWidth,changedText.TextHeight*4)+margin*2,Math.Max(changedText.ActualHeight,changedText.TextHeight*2)+margin*2);}
                group.Description = "LA批注 " + data.Number; WriteMaster(group, tr, data); tr.Commit(); return true;
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

        private static string FormatText(AnnotationData d,AnnotationSettings s) => $"\\H{s.HeaderHeight:0.###};{Escape(d.Number)}    {Escape(d.Discipline)}    {Escape(d.Author)}    {Escape(d.Date)}\\P\\H{s.TextHeight:0.###};{Escape(d.Content).Replace("\r\n", "\\P").Replace("\n", "\\P")}\\P\\H{s.SecondLineHeight:0.###};状态: {Escape(d.Status)}";
        private static string Escape(string value) => (value ?? "").Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
        internal static Polyline BuildBox(Point3d p, double w, double h) { var x=new Polyline();x.AddVertexAt(0,new Point2d(p.X,p.Y),0,0,0);x.AddVertexAt(1,new Point2d(p.X+w,p.Y),0,0,0);x.AddVertexAt(2,new Point2d(p.X+w,p.Y+h),0,0,0);x.AddVertexAt(3,new Point2d(p.X,p.Y+h),0,0,0);x.Closed=true;return x; }
        /// <summary>根据当前视图扭转角旋转文字和边框，使批注在旋转视图中保持可读。</summary>
        private static void RotateAnnotationByViewTwist(MText text, Polyline box, Point3d origin)
        {
            try
            {
                var twist = Convert.ToDouble(CadSystemVariable("VIEWTWIST"));
                if (Math.Abs(twist) < 1e-9) return;
                text.Rotation = twist;
                var cos = Math.Cos(twist); var sin = Math.Sin(twist);
                for (var i = 0; i < 4; i++)
                {
                    var pt = box.GetPoint2dAt(i);
                    var dx = pt.X - origin.X; var dy = pt.Y - origin.Y;
                    box.SetPointAt(i, new Point2d(origin.X + dx * cos - dy * sin, origin.Y + dx * sin + dy * cos));
                }
            }
            catch { /* 获取系统变量失败时静默跳过，不中断批注创建 */ }
        }
        /// <summary>构建云线多段线，支持矩形/菱形/椭圆三种外形，开启 CloudAutoFit 时自适应弧段数。</summary>
        internal static Polyline BuildCloud(Point2d min, Point2d max, AnnotationSettings settings)
        {
            var radius=Math.Max(settings.CloudRadius,0.001);
            var w=Math.Max(0,max.X-min.X);var h=Math.Max(0,max.Y-min.Y);
            var perimeter=ComputePerimeter(w,h,settings.Shape);
            if(perimeter<1e-9){var empty=new Polyline();empty.AddVertexAt(0,min,0,0,0);empty.AddVertexAt(1,max,0,0,0);return empty;}
            var spacing=Math.Max(0.1,radius*2);
            if(settings.CloudAutoFit){var desired=Math.Max(20,Math.Min(160,(int)Math.Ceiling(perimeter/Math.Max(settings.TextHeight*1.2,0.1))));spacing=perimeter/desired;}
            // 菱形：四个顶点 + 自适应弧间距
            if(settings.Shape=="菱形"){var cx=(min.X+max.X)/2;var cy=(min.Y+max.Y)/2;return BuildScallopedPath(new[]{new Point2d(cx,min.Y),new Point2d(max.X,cy),new Point2d(cx,max.Y),new Point2d(min.X,cy)},spacing,settings.CloudStyle);}
            // 椭圆：自适应采样点数
            if(settings.Shape=="椭圆"){var cx=(min.X+max.X)/2;var cy=(min.Y+max.Y)/2;var rx=w/2;var ry=h/2;var count=Math.Max(20,Math.Min(160,(int)Math.Ceiling(perimeter/spacing)));var points=new List<Point2d>();for(var i=0;i<count;i++){var a=2*Math.PI*i/count;points.Add(new Point2d(cx+rx*Math.Cos(a),cy+ry*Math.Sin(a)));}return BuildScallopedVertices(points,settings.CloudStyle);}
            // 矩形：四边均匀布点
            var p=new Polyline();
            var nx=Math.Max(4,(int)Math.Ceiling(w/spacing));var ny=Math.Max(4,(int)Math.Ceiling(h/spacing));
            const int maxVertices=400;var total=2*(nx+ny);if(total>maxVertices){var scale=(double)maxVertices/total;nx=Math.Max(4,(int)Math.Floor(nx*scale));ny=Math.Max(4,(int)Math.Floor(ny*scale));}
            var pts=new List<Point2d>();
            for(var i=0;i<nx;i++)pts.Add(new Point2d(min.X+w*i/nx,min.Y));for(var i=0;i<ny;i++)pts.Add(new Point2d(max.X,min.Y+h*i/ny));for(var i=0;i<nx;i++)pts.Add(new Point2d(max.X-w*i/nx,max.Y));for(var i=0;i<ny;i++)pts.Add(new Point2d(min.X,max.Y-h*i/ny));
            for(var i=0;i<pts.Count;i++)p.AddVertexAt(i,pts[i],settings.CloudStyle=="等宽"?-0.55:(i%2==0?-0.35:-0.7),0,0);p.Closed=true;return p;
        }
        /// <summary>计算各外形的近似周长，用于自适应弧段数。</summary>
        private static double ComputePerimeter(double w, double h, string shape)
        {
            if(shape=="菱形") return 2*Math.Sqrt(w*w+h*h);        // 菱形周长 = 2√(w²+h²)
            if(shape=="椭圆") return Math.PI*(w+h)/2*1.05;        // 椭圆近似周长
            return 2*(w+h);                                        // 矩形周长
        }
        internal static Point2d ClosestCorner(Point2d min,Point2d max,Point3d p){var target=new Point2d(p.X,p.Y);var a=new[]{min,new Point2d(max.X,min.Y),max,new Point2d(min.X,max.Y)};return a.OrderBy(x=>x.GetDistanceTo(target)).First();}
        private static bool Contains(Group group,ObjectId id){foreach(ObjectId member in group.GetAllEntityIds())if(member==id)return true;return false;}
        private static void Add(BlockTableRecord space,Transaction tr,Entity e,ObjectIdCollection ids,AnnotationSettings s,string id,string role,short color){e.Layer=EffectiveLayer(s);e.Color=Color.FromColorIndex(ColorMethod.ByAci,color);if(e is Polyline p&&s.LineWidth>0&&(role=="cloud"||role=="leader"))p.ConstantWidth=s.LineWidth;e.XData=new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName,AnnotationCodec.AppName),new TypedValue((int)DxfCode.ExtendedDataAsciiString,id),new TypedValue((int)DxfCode.ExtendedDataAsciiString,role));ids.Add(space.AppendEntity(e));tr.AddNewlyCreatedDBObject(e,true);}
        private static bool TryGetRole(Entity e,out string role){role=null;var rb=e.GetXDataForApplication(AnnotationCodec.AppName);if(rb==null)return false;var a=rb.AsArray();if(a.Length<3)return false;role=a[2].Value as string;return role!=null;}
        private static void ResizeBox(Polyline box,Point3d p,double w,double h){box.SetPointAt(0,new Point2d(p.X,p.Y));box.SetPointAt(1,new Point2d(p.X+w,p.Y));box.SetPointAt(2,new Point2d(p.X+w,p.Y+h));box.SetPointAt(3,new Point2d(p.X,p.Y+h));}
        private static Polyline BuildScallopedPath(Point2d[] corners,double spacing,string style){var points=new List<Point2d>();for(var i=0;i<corners.Length;i++){var a=corners[i];var b=corners[(i+1)%corners.Length];var count=Math.Min(100,Math.Max(4,(int)Math.Ceiling(a.GetDistanceTo(b)/Math.Max(spacing*2,0.1))));for(var j=0;j<count;j++)points.Add(new Point2d(a.X+(b.X-a.X)*j/count,a.Y+(b.Y-a.Y)*j/count));}return BuildScallopedVertices(points,style);}
        private static Polyline BuildScallopedVertices(IList<Point2d> points,string style){var p=new Polyline();for(var i=0;i<points.Count;i++)p.AddVertexAt(i,points[i],style=="等宽"?-0.55:(i%2==0?-0.35:-0.7),0,0);p.Closed=true;return p;}
        private static string EffectiveLayer(AnnotationSettings s){var parts=new List<string>{s.LayerName};if(s.LayerAppendDate&&s.DateBeforeName)parts.Add(DateTime.Today.ToString("yyyyMMdd"));if(s.LayerAppendName)parts.Add(s.DefaultAuthor);if(s.LayerAppendDate&&!s.DateBeforeName)parts.Add(DateTime.Today.ToString("yyyyMMdd"));var connector=s.Connector=="无"?"":s.Connector;return string.Join(connector,parts.Where(x=>!string.IsNullOrWhiteSpace(x)));}
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
        private static void EnsureRegApp(Database db,Transaction tr){var t=(RegAppTable)tr.GetObject(db.RegAppTableId,OpenMode.ForRead);if(t.Has(AnnotationCodec.AppName))return;t.UpgradeOpen();var r=new RegAppTableRecord{Name=AnnotationCodec.AppName};t.Add(r);tr.AddNewlyCreatedDBObject(r,true);}
        private static void EnsureLayer(Database db,Transaction tr,AnnotationSettings s){var name=EffectiveLayer(s);var t=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);if(t.Has(name))return;t.UpgradeOpen();var r=new LayerTableRecord{Name=name,Color=Color.FromColorIndex(ColorMethod.ByAci,s.CloudColor),IsPlottable=s.Plottable};t.Add(r);tr.AddNewlyCreatedDBObject(r,true);}
    }
}
