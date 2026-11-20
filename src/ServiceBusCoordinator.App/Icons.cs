using Avalonia.Media;

namespace ServiceBusCoordinator.App;

/// <summary>Small vector icons (16×16 design grid) used in the tree and toolbars.</summary>
public static class Icons
{
    public static readonly Geometry Namespace = Geometry.Parse("F1 M2,2 H14 V7 H2 Z M4,4 V5 H6 V4 Z M2,9 H14 V14 H2 Z M4,11 V12 H6 V11 Z");
    public static readonly Geometry Folder = Geometry.Parse("M1,3 H6 L8,5 H15 V13 H1 Z");
    public static readonly Geometry Queue = Geometry.Parse("M2,3 H14 V5 H2 Z M2,7 H14 V9 H2 Z M2,11 H14 V13 H2 Z");
    public static readonly Geometry Topic = Geometry.Parse("M1,7 H5 L9,3 H15 V5 H10 L7,8 L10,11 H15 V13 H9 L5,9 H1 Z");
    public static readonly Geometry Subscription = Geometry.Parse("M7,1 H9 V7 H12 L8,11 L4,7 H7 Z M1,10 H3 V13 H13 V10 H15 V15 H1 Z");
    public static readonly Geometry AddEntity = Geometry.Parse("M1,3 H10 V5 H1 Z M1,7 H10 V9 H1 Z M1,11 H7 V13 H1 Z M11,8 H13 V10 H15 V12 H13 V14 H11 V12 H9 V10 H11 Z");
    public static readonly Geometry Plus =Geometry.Parse("M7,2 H9 V7 H14 V9 H9 V14 H7 V9 H2 V7 H7 Z");
    public static readonly Geometry Refresh = Geometry.Parse("M13.5,4.5 A6.5,6.5 0 1 0 14.5,9 H12.4 A4.5,4.5 0 1 1 11.9,5.9 L9.5,8 H15 V2.5 Z");
    public static readonly Geometry Settings = Geometry.Parse("M2,3 H14 V5 H2 Z M2,7 H14 V9 H2 Z M2,11 H14 V13 H2 Z M4,2 H7 V6 H4 Z M9,6 H12 V10 H9 Z M5,10 H8 V14 H5 Z");
    public static readonly Geometry NewFolder = Geometry.Parse("M1,3 H6 L8,5 H15 V13 H1 Z M3,7 V11 H13 V7 Z M7,7.5 H9 V8.5 H10.5 V9.5 H9 V10.5 H7 V9.5 H5.5 V8.5 H7 Z");
    public static readonly Geometry Trash = Geometry.Parse("M5,1 H11 V3 H14 V5 H2 V3 H5 Z M3,6 H13 L12,15 H4 Z");
    public static readonly Geometry Send = Geometry.Parse("M1,7 L15,1 L9,15 L7,9 Z");
    public static readonly Geometry Export = Geometry.Parse("M7,11 H9 V5 H12 L8,1 L4,5 H7 Z M1,10 H3 V13 H13 V10 H15 V15 H1 Z");
    public static readonly Geometry Resubmit = Geometry.Parse("M6,2 L1,6.5 L6,11 V8 H10 C11.7,8 13,9.3 13,11 V14 H15 V11 C15,8.2 12.8,6 10,6 H6 Z");
    public static readonly Geometry Filter = Geometry.Parse("M1,2 H15 L10,8 V14 L6,12 V8 Z");
    public static readonly Geometry Download = Geometry.Parse("M7,1 H9 V8 H12 L8,12 L4,8 H7 Z M1,13 H15 V15 H1 Z");
    public static readonly Geometry CollapseAll = Geometry.Parse("M3,7.2 L8,3 L13,7.2 L11.8,8.6 L8,5.4 L4.2,8.6 Z M3,12.2 L8,8 L13,12.2 L11.8,13.6 L8,10.4 L4.2,13.6 Z");
    public static readonly Geometry More = Geometry.Parse("M2,6.5 H5 V9.5 H2 Z M6.5,6.5 H9.5 V9.5 H6.5 Z M11,6.5 H14 V9.5 H11 Z");
    public static readonly Geometry Lightning = Geometry.Parse("M9,1 L3,9 H7 L6,15 L13,6 H9 Z");
}
