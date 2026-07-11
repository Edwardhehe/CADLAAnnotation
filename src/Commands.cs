using System;
using LAAnnotation.Views;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Runtime;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace LAAnnotation
{
    /// <summary>CAD 命令集合：绘制/编辑/删除批注、设置、重载菜单。</summary>
    public sealed class Commands
    {
        /// <summary>创建批注：选点 → 填表 → 创建实体组（云线+引线+文字+边框）。</summary>
        [CommandMethod("LA_PZ_NOTE", CommandFlags.Modal)]
        public void CreateAnnotation()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            var settings = SettingsStore.Load();
            var data = new AnnotationData { Author = settings.DefaultAuthor, Discipline = settings.DefaultDiscipline };
            var effectiveSettings=AnnotationService.ResolveEffectiveSettings(doc,settings,data);
            if(!AnnotationService.PromptGeometry(doc,effectiveSettings,out var first,out var second,out var textLocation))return;
            if(settings.FontAutoFit)doc.Editor.WriteMessage($"\nLA批注自适应字高: {effectiveSettings.TextHeight:0.###}，云线半径: {effectiveSettings.CloudRadius:0.###}");
            if (settings.AutoNumber) data.Number = "LA-" + settings.NextNumber.ToString("D3");
            var form = new AnnotationWindow(data, false);if (CadDialog.ShowModal(form) != true) return;
            try
            {
                if (AnnotationService.Create(doc, data, effectiveSettings, first, second, textLocation))
                {
                    if (settings.AutoNumber) { settings.NextNumber++; SettingsStore.Save(settings); }
                    doc.Editor.WriteMessage("\nLA批注已创建: " + data.Number);
                }
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\nLA批注创建失败: " + ex.Message); }
        }

        /// <summary>编辑批注：优先使用已选实体，否则让用户点选。</summary>
        [CommandMethod("LA_PZ_EDIT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void EditAnnotation()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            var implied = doc.Editor.SelectImplied();
            ObjectId id;
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0) id = implied.Value.GetObjectIds()[0];
            else
            {
                var options = new PromptEntityOptions("\n选择要编辑的 LA批注: ");
                var result = doc.Editor.GetEntity(options); if (result.Status != PromptStatus.OK) return; id = result.ObjectId;
            }
            EditById(doc, id);
        }

        /// <summary>删除批注：删除整个实体组（云线+引线+文字+边框），支持 UNDO。</summary>
        [CommandMethod("LA_PZ_DELETE", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void DeleteAnnotation()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            var implied = doc.Editor.SelectImplied(); ObjectId target;
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0) target = implied.Value.GetObjectIds()[0];
            else { var result = doc.Editor.GetEntity("\n选择要删除的 LA批注: "); if (result.Status != PromptStatus.OK) return; target = result.ObjectId; }
            try
            {
                if (!AnnotationService.Delete(doc, target)) { doc.Editor.WriteMessage("\n所选对象不是有效的 LA批注。"); return; }
                doc.Editor.WriteMessage("\nLA批注已删除，可使用 UNDO 恢复。");
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n删除失败: " + ex.Message); }
        }

        [CommandMethod("LA_PZ_SETTINGS", CommandFlags.Modal)]
        public void Settings()
        {
            CadDialog.ShowModal(new SettingsWindow(SettingsStore.Load()));
        }

        [CommandMethod("LA_PZ_MENU", CommandFlags.Modal)]
        public void ReloadMenu()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            MenuInstaller.Ensure(out var message);
            doc?.Editor.WriteMessage("\n" + message);
        }

        internal static bool EditById(Document doc, ObjectId id)
        {
            AnnotationData data;
            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || !AnnotationService.TryReadFromEntity(tr, entity, out data)) { doc.Editor.WriteMessage("\n所选对象不是有效的 LA批注，或批注数据已损坏。"); return false; }
                }
                var form = new AnnotationWindow(data, true);if (CadDialog.ShowModal(form) != true) return false;
                if (!AnnotationService.Update(doc, id, data)) { doc.Editor.WriteMessage("\n批注更新失败：对象关联已改变。"); return false; }
                doc.Editor.WriteMessage("\nLA批注已更新: " + data.Number); return true;
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n编辑失败: " + ex.Message); return false; }
        }
    }
}
