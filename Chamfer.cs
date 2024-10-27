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

        private ObservableCollection<Geometry> _selectedFeatures = new();
        public ObservableCollection<Geometry> SelectedFeatures
        {
            get { return _selectedFeatures; }
        }

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

                Geometry selected_seg_geom = PolylineBuilderEx.CreatePolyline(selected_segment) as Geometry;

                if (SelectedFeatures.Count > 1 || SelectedFeatures.Count == 0)
                {
                    SelectedFeatures.Clear();
                    SelectedFeatures.Add(selected_seg_geom);
                    lock (_lock)
                    {
                        if (_graphic != null)
                            _graphic.Dispose();

                        _graphic = this.AddOverlay(selected_seg_geom, _lineSymbol.MakeSymbolReference());
                    }
                }
                else if (SelectedFeatures.Count == 1)
                {
                    SelectedFeatures.Add(selected_seg_geom);
                    var merged_geoms = SelectedFeatures.Aggregate((accumulator, item) => {
                        return GeometryEngine.Instance.Union(accumulator, item);
                    });
                    lock (_lock)
                    {
                        this.UpdateOverlay(_graphic, merged_geoms, _lineSymbol.MakeSymbolReference());
                    }
                }

                //IDisposable graphic = null;
                //lock (_lock)
                //{
                //    graphic = _graphic;
                //}

                //this.UpdateOverlay(graphic, filtered_geom, _lineSymbol.MakeSymbolReference());

                return true;


                //var bufferd_point = GeometryEngine.Instance.Buffer(point_selection_center, )
                //SpatialQueryFilter selection_type = new SpatialQueryFilter() { FilterGeometry = point_selection_center, SpatialRelationship= SpatialRelationship.Touches };
            });

            return Task.FromResult(true);
        }

        protected override Task<bool> OnToolDeactivateAsync(bool hasMapViewChanged)
        {
            _lineSymbol = null;
            _selectedFeatures = new();

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
    }
}
