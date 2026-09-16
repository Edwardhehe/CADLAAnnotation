using System;
using System.Windows;

namespace GMAnnotation.Views
{
    internal static class WindowSizing
    {
        public static void FitToWorkArea(Window window,double widthRatio=0.92,double heightRatio=0.88)
        {
            var area=SystemParameters.WorkArea;window.MaxWidth=Math.Max(window.MinWidth,area.Width-24);window.MaxHeight=Math.Max(window.MinHeight,area.Height-24);window.Width=Math.Min(window.Width,area.Width*widthRatio);window.Height=Math.Min(window.Height,area.Height*heightRatio);
        }
    }
}
