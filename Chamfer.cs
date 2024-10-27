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
using ArcGIS.Desktop.KnowledgeGraph;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using Geometry = ArcGIS.Core.Geometry.Geometry;

namespace Chamfer
{
    internal class Chamfer : MapTool
    {
        private IDisposable _graphic = null;
        private CIMLineSymbol _lineSymbol = null;

        private ObservableCollection<Polyline> _selectedSegments = new();

        private static readonly object _lock = new object();

        public Chamfer()
        {
            IsSketchTool = true;
            SketchType = SketchGeometryType.Point;
            SketchOutputMode = SketchOutputMode.Map;
            UseSnapping = true;
        }

        protected override async Task OnToolActivateAsync(bool active)
        {
            if (_lineSymbol == null)
            {
                await QueuedTask.Run(() =>
                {
                    _lineSymbol = SymbolFactory.Instance.ConstructLineSymbol(CIMColor.CreateRGBColor(128, 128, 128), 4.0, SimpleLineStyle.Solid);
                });
            }
        }

        // TODO: Implement esc handling to further mimick fillet behavior
        protected override Task<bool> OnSketchCompleteAsync(Geometry point_selection)
        {
            QueuedTask.Run(() =>
            {
                // Get visually selected features from active map
                var selected_features = MapView.Active.GetFeatures(point_selection, true);
                if (selected_features.IsEmpty)
                    return false;

                var insp = new Inspector();
                var features_oids = selected_features[selected_features.ToDictionary().Keys.First()];
                insp.Load(selected_features.ToDictionary().First().Key, features_oids.First());
                if (insp.HasAnnotationAttributes)
                    return false;

                Geometry selected_geom = insp.Shape;
                Polyline filtered_geom = insp.Shape.GeometryType == GeometryType.Polygon ? GeometryEngine.Instance.Boundary(insp.Shape) as Polyline : insp.Shape as Polyline;
                if (filtered_geom == null)
                    return false;

                ReadOnlyPartCollection segments = filtered_geom.Parts;
                Segment selected_segment = segments
                    .SelectMany(collection => collection).ToList()
                    .OrderBy(segment => GeometryEngine.Instance.Distance(point_selection, PolylineBuilderEx.CreatePolyline(segment)))
                    .FirstOrDefault();

                Polyline selected_seg_geom = PolylineBuilderEx.CreatePolyline(selected_segment);

                // Case: none selected or two segments selected
                // TOTO: separate these cases to make two selected the commit operation case
                if (_selectedSegments.Count > 1 || _selectedSegments.Count == 0)
                {
                    _selectedSegments.Clear();
                    _selectedSegments.Add(selected_seg_geom);
                    lock (_lock)
                    {
                        if (_graphic != null)
                            _graphic.Dispose();

                        _graphic = this.AddOverlay(selected_seg_geom, _lineSymbol.MakeSymbolReference());
                    }
                }
                // Case: one segment already selected
                else if (_selectedSegments.Count == 1)
                {
                    MapPoint int_point = ChamferLines(_selectedSegments[0] as Polyline, selected_seg_geom as Polyline);
                    // Case: No intersection found (parallel lines)
                    if (int_point == null)
                        return false;
                    _selectedSegments.Add(selected_seg_geom);
                    var merged_geoms = GeometryEngine.Instance.Union(_selectedSegments[0], selected_seg_geom) as Polyline;
                    lock (_lock)
                    {
                        this.UpdateOverlay(_graphic, merged_geoms, _lineSymbol.MakeSymbolReference());
                    }

                    // Deubug output int point
                    var seg1 = _selectedSegments[0].Parts.FirstOrDefault().FirstOrDefault();
                    var seg2 = _selectedSegments[1].Parts.FirstOrDefault().FirstOrDefault();
                    var test_line = 
                    GeometryEngine.Instance.Union(
                        PolylineBuilderEx.CreatePolyline(new[] { seg1.EndPoint, int_point }, selected_segment.SpatialReference),
                        PolylineBuilderEx.CreatePolyline(new[] { seg2.EndPoint, int_point }, selected_segment.SpatialReference)
                    );
                    insp.Shape = test_line;

                    lock (_lock)
                    {
                        if (_graphic != null)
                            _graphic.Dispose();

                        _graphic = this.AddOverlay(test_line, _lineSymbol.MakeSymbolReference());
                    }

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
            _lineSymbol = null;
            _selectedSegments = new();

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

        #region Internal Functions

        private static double? GetSlope(double x1, double y1, double x2, double y2)
        {
            if (x2 == x1)
                return null; // Vertical line, undefined slope
            return (y2 - y1) / (x2 - x1);
        }

        // Intended to operate on two polylines which each only contain one segment
        private static MapPoint ChamferLines(Polyline line1, Polyline line2, float length=0)
        {

            if (line1.SpatialReference != line2.SpatialReference)
                return null;

            // TODO: case if lines actually intersect
            //MapPoint intersect_point = GeometryEngine.Instance.Intersection(line1, line2, GeometryDimensionType.EsriGeometry0Dimension) as MapPoint;

            // Find theoretical intersection point between two segments (assumes straight line)
            // TODO: add case for curved segments (tangent line @ endpoint?)
            List<double?> slopes = new();
            List<double> intercepts = new();
            foreach (Polyline line in new[] { line1, line2 })
            {
                Segment line_segment = line.Parts.FirstOrDefault().FirstOrDefault();
                double? slope = GetSlope(line_segment.StartCoordinate.X, line_segment.StartCoordinate.Y, line_segment.EndCoordinate.X, line_segment.EndCoordinate.Y);
                slopes.Add(slope);
                intercepts.Add
                (
                    (slope == null)
                    ? line_segment.StartCoordinate.X
                    : line_segment.StartCoordinate.Y - (slope.Value * line_segment.StartCoordinate.X)
                );
            }

            double int_x;
            double int_y;

            // Case: parallel lines (also catches parallel vertical lines)
            if (slopes[0] == slopes[1])
                return null;
            // Case: one vertical line
            int null_idx = slopes.IndexOf(null);
            if (null_idx != -1)
            {
                int non_null_idx = 1 - null_idx;
                int_x = intercepts[null_idx];
                int_y = (slopes[non_null_idx].Value * int_x) + intercepts[non_null_idx];
            }
            // Case: no vertical lines
            else
            {
                int_x = (intercepts[1] - intercepts[0]) / (slopes[0].Value - slopes[1].Value);
                int_y = (slopes[0].Value * int_x) + intercepts[0];
            }

            MapPoint int_point = MapPointBuilderEx.CreateMapPoint(int_x, int_y, line1.SpatialReference);

            return int_point;
        }

        #endregion
    }
}
