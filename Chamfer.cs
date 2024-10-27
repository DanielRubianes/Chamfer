using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Data.UtilityNetwork.Trace;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Editing.Attributes;
using ArcGIS.Desktop.Extensions;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Internal.Core;
using ArcGIS.Desktop.KnowledgeGraph;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using static System.Formats.Asn1.AsnWriter;
using Geometry = ArcGIS.Core.Geometry.Geometry;

namespace Chamfer
{
    internal class Chamfer : MapTool
    {
        enum TrackingState
        {
            NotTracking = 0,
            CanTrack,
            Tracking
        }

        private IDisposable _graphic = null;
        private CIMLineSymbol _solid_line = null;
        private CIMLineSymbol _dashed_line = null;

        private List<InfiniteLine> _selected_segments = new();

        private System.Windows.Point? _lastLocation = null;
        private System.Windows.Point? _workingLocation = null;

        private TrackingState _trackingMouseMove = TrackingState.NotTracking;
        private static readonly object _lock = new object();

        public class InfiniteLine
        // Contains a slope-intercept defenition of a two-point line, as well as the endpoints used to construct said line
        // Intended to operate on two-point line segments
        // TODO: Implement double.PositiveInfinity rather than null railroad
        {
            public readonly Polyline DisplayGeometry;
            public readonly MapPoint StartPoint;
            public readonly MapPoint EndPoint;
            public readonly double? Slope;
            public readonly double Intercept;

            public static InfiniteLine Average(InfiniteLine line1, InfiniteLine line2)
            {
                var x_component = ((line1.EndPoint.X - line1.StartPoint.X) + (line2.EndPoint.X - line2.StartPoint.X)) / 2;
                var y_component = ((line1.EndPoint.Y - line1.StartPoint.Y) + (line2.EndPoint.Y - line2.StartPoint.Y)) / 2;
                return new InfiniteLine
                (
                    MapPointBuilderEx.CreateMapPoint(0, 0, line1.StartPoint.SpatialReference),
                    MapPointBuilderEx.CreateMapPoint(x_component, y_component, line1.StartPoint.SpatialReference)
                );
            }

            public static InfiniteLine rotate90(InfiniteLine line)
            {
                var x_component = -1 * (line.EndPoint.X - line.StartPoint.X);
                var y_component = (line.EndPoint.Y - line.StartPoint.Y);
                return new InfiniteLine(line.StartPoint, MapPointBuilderEx.CreateMapPoint(x_component, y_component, line.EndPoint.SpatialReference));
            }

            public InfiniteLine(Polyline line) : this(line.Points[0], line.Points[^1], line) { }
            public InfiniteLine(Segment seg) : this(seg.StartPoint, seg.EndPoint) { }
            public InfiniteLine(MapPoint start_point, MapPoint end_point, Polyline display_geometry = null)
            {
                DisplayGeometry = (display_geometry == null)
                    ? PolylineBuilderEx.CreatePolyline(new[] { start_point, end_point }, start_point.SpatialReference)
                    : display_geometry;
                StartPoint = start_point;
                EndPoint = end_point;
                Slope = (end_point.X == start_point.X)
                    ? null // Vertical line, undefined slope
                    : (end_point.Y - start_point.Y) / (end_point.X - start_point.X);
                Intercept = (Slope == null)
                    ? start_point.X // Vertical line; X is constant
                    : start_point.Y - (Slope.Value * start_point.X);
            }
        }

        public Chamfer()
        {
            IsSketchTool = true;
            SketchType = SketchGeometryType.Point;
            SketchOutputMode = SketchOutputMode.Map;
            UseSnapping = true;
        }

        protected override async Task OnToolActivateAsync(bool active)
        {
            _lastLocation = null;
            _workingLocation = null;
            _trackingMouseMove = TrackingState.NotTracking;
            if (_solid_line == null || _dashed_line == null)
            {
                await QueuedTask.Run(() =>
                {
                    _solid_line = SymbolFactory.Instance.ConstructLineSymbol(CIMColor.CreateRGBColor(128, 128, 128), 3.0, SimpleLineStyle.Solid);
                    _dashed_line = SymbolFactory.Instance.ConstructLineSymbol(CIMColor.CreateRGBColor(0, 0, 0), 3.0, SimpleLineStyle.Dash);
                });
            }
        }

        // TODO: Implement esc handling to further mimic fillet behavior
        protected override Task<bool> OnSketchCompleteAsync(Geometry point_selection)
        {
            QueuedTask.Run(() =>
            {
                var selected_features = MapView.Active.GetFeatures(point_selection, true); // Get visually selected features from active map
                if (selected_features.IsEmpty) // Exit if no features selected
                    return false;

                var insp = new Inspector();
                var features_oids = selected_features[selected_features.ToDictionary().Keys.First()];
                insp.Load(selected_features.ToDictionary().First().Key, features_oids.First());
                if (insp.HasAnnotationAttributes) // Exit if feature selected is annotation
                    return false;

                Polyline outline_geom =
                    insp.Shape.GeometryType == GeometryType.Polygon
                    ? GeometryEngine.Instance.Boundary(insp.Shape) as Polyline
                    : insp.Shape as Polyline;

                if (outline_geom == null) // Exit if null or non-line geometry
                    return false;
                
                // Find closest segment (polyline part) to the selection point
                Segment selected_segment = outline_geom.Parts
                    .SelectMany(segment => segment).ToList()
                    .OrderBy(segment => GeometryEngine.Instance.Distance(point_selection, PolylineBuilderEx.CreatePolyline(segment)))
                    .FirstOrDefault();
                InfiniteLine selected_line = new InfiniteLine(selected_segment);

                // Case: No prior selection
                if (_selected_segments.Count == 0)
                {
                    _selected_segments.Clear();
                    _selected_segments.Add(selected_line);
                    lock (_lock)
                    {
                        if (_graphic != null)
                            _graphic.Dispose();
                        _graphic = this.AddOverlay(selected_line.DisplayGeometry, _solid_line.MakeSymbolReference());
                    }
                    _trackingMouseMove = TrackingState.NotTracking;
                }
                // Case: One segment already selected
                else if (_selected_segments.Count == 1)
                {
                    Polyline extensions = ChamferLines(_selected_segments[0], selected_line);
                    // Case: No intersection found (parallel lines)
                    if (extensions == null)
                        return false;
                    _selected_segments.Add(selected_line);
                    lock (_lock)
                    {
                        this.UpdateOverlay(_graphic, extensions, _dashed_line.MakeSymbolReference());
                    }
                    _trackingMouseMove = TrackingState.CanTrack;
                }
                // Case: Two segments already selected
                // This is a stub to allow repeated testing of initial selection
                else if (_selected_segments.Count > 1)
                {
                    _selected_segments.Clear();
                    lock (_lock)
                    {
                        if (_graphic != null)
                            _graphic.Dispose();
                    }
                    _trackingMouseMove = TrackingState.NotTracking;
                }
                // Edit operation syntax
                //var op = new EditOperation()
                //{
                //    Name = "Test Operation",
                //    SelectModifiedFeatures = true,
                //    SelectNewFeatures = false
                //};
                //op.Modify(insp);
                //return op.Execute();
                return true;
            });
            return Task.FromResult(true);
        }

        protected override Task<bool> OnToolDeactivateAsync(bool hasMapViewChanged)
        {
            _solid_line = null;
            _dashed_line = null;
            _selected_segments = new();

            lock (_lock)
            {
                if (_graphic != null)
                {
                    _graphic.Dispose();
                    _graphic = null;
                }
            }

            return Task.FromResult(true);
        }

        protected override async void OnToolMouseMove(MapViewMouseEventArgs e)
        {
            //All of this logic is to avoid unnecessarily updating the graphic position
            //for ~every~ mouse move. We skip any "intermediate" points in-between rapid
            //mouse moves.
            lock (_lock)
            {
                if (_trackingMouseMove == TrackingState.NotTracking)
                    return;
                else
                {
                    if (_workingLocation.HasValue)
                    {
                        _lastLocation = e.ClientPoint;
                        return;
                    }
                    else
                    {
                        _lastLocation = e.ClientPoint;
                        _workingLocation = e.ClientPoint;
                    }
                }
                _trackingMouseMove = TrackingState.Tracking;
            }
            //The code "inside" the QTR will execute for all points that
            //get "buffered" or "queued". This avoids having to spin up a QTR
            //for ~every~ point of ~every mouse move.

            await QueuedTask.Run(() =>
            {
                while (true)
                {
                    System.Windows.Point? point;
                    IDisposable graphic = null;
                    MapPoint mouse_point = null;
                    lock (_lock)
                    {
                        point = _lastLocation;
                        _lastLocation = null;
                        _workingLocation = point;
                        if (point == null || !point.HasValue)
                        {
                            //No new points came in while we updated the overlay
                            _workingLocation = null;
                            break;
                        }
                        else if (_graphic == null)
                        {
                            //conflict with the mouse down,
                            //If this happens then we are done. A new line and point will be
                            //forthcoming from the SketchCompleted callback
                            _trackingMouseMove = TrackingState.NotTracking;
                            break;
                        }
                        graphic = _graphic;
                        if (point.HasValue)
                            mouse_point = this.ActiveMapView.ClientToMap(point.Value);
                    }
                    if (mouse_point != null)
                    {
                        //update the graphic overlay

                        Polyline preview_line = ChamferLines(_selected_segments[0], _selected_segments[1], mouse_point);

                        this.UpdateOverlay(graphic, preview_line, _dashed_line.MakeSymbolReference());
                    }
                }
            });
        }

        #region Internal Functions

        //private static double? GetSlope(double x1, double y1, double x2, double y2)
        //{
        //    if (x2 == x1)
        //        return null; // Vertical line, undefined slope
        //    return (y2 - y1) / (x2 - x1);
        //}

        // Find theoretical intersection point between two segments (assumes straight lines)
        // TODO: add case for curved segments (tangent line @ endpoint?)
        private static MapPoint GetIntersectionPoint(InfiniteLine line1, InfiniteLine line2)
        {
            // TODO: Add case for existing intersection point
            if (line1.StartPoint.SpatialReference != line2.StartPoint.SpatialReference)
                return null;
            double int_x;
            double int_y;
            // Case: Parallel lines (also catches parallel vertical lines)
            if (line1.Slope == line2.Slope)
                return null;
            // Case: One vertical line
            if (line1.Slope == null || line2.Slope == null)
            {
                InfiniteLine vertical_line = (line1.Slope == null) ? line1 : line2;
                InfiniteLine non_vertical_line = (line2.Slope == null) ? line1 : line1;
                int_x = vertical_line.Intercept;
                int_y = (non_vertical_line.Slope.Value * int_x) + non_vertical_line.Intercept;
            }
            // Case: No vertical lines
            else
            {
                int_x = (line2.Intercept - line1.Intercept) / (line1.Slope.Value - line2.Slope.Value);
                int_y = (line1.Slope.Value * int_x) + line1.Intercept;
            }

            MapPoint int_point = MapPointBuilderEx.CreateMapPoint(int_x, int_y, line1.StartPoint.SpatialReference);

            return int_point;
        }

        // Intended to operate on two polylines which each only contain one segment
        private static Polyline ChamferLines(InfiniteLine line1, InfiniteLine line2, MapPoint mouse_point = null)
        {
            MapPoint intersection_point = GetIntersectionPoint(line1, line2);
            if (intersection_point == null) // This will filter out parallel lines, including two with null slope
                return null;
            List<Polyline> theoretical_extensions = new();
            foreach (InfiniteLine line in new[] { line1, line2 })
            {
                MapPoint closest_point = new[] {line.StartPoint, line.EndPoint}
                    .OrderBy(point => GeometryEngine.Instance.Distance(intersection_point, point))
                    .FirstOrDefault();
                theoretical_extensions.Add(PolylineBuilderEx.CreatePolyline(new[] { closest_point, intersection_point }, line.StartPoint.SpatialReference));
            }
            if (mouse_point != null)
            {
                //double? avg_slope = (line1.Slope == null) ? line2.Slope
                //    : (line2.Slope == null) ? line1.Slope
                //    : (line1.Slope.Value + line2.Slope.Value) / 2;

                //double angle1 = line1.Slope.HasValue ? Math.Atan(line1.Slope.Value) : Math.PI / 2;
                //double angle2 = line2.Slope.HasValue ? Math.Atan(line2.Slope.Value) : Math.PI / 2;

                //double avg_angle = (angle1 / angle2) / 2;

                //double avg_slope = Math.Tan(avg_angle);

                //MapPoint translated_mouse_point = GeometryEngine.Instance.Move(mouse_point, 1, avg_slope) as MapPoint;
                //InfiniteLine mouse_line = new InfiniteLine(mouse_point, translated_mouse_point);

                var mouse_line = InfiniteLine.rotate90(line1);
                var int_pt1 = GetIntersectionPoint(line1, mouse_line);
                var int_pt2 = GetIntersectionPoint(line2, mouse_line);
                return PolylineBuilderEx.CreatePolyline(new[] { int_pt1, int_pt2 }, intersection_point.SpatialReference);
            }
            return GeometryEngine.Instance.Union(new[] { theoretical_extensions[0], theoretical_extensions[1] }) as Polyline;
        }

        #endregion
    }
}
