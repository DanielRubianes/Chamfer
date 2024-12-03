using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Extensions;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Controls;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.KnowledgeGraph;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Chamfer
{
    internal class ChamferControlViewModel : EmbeddableControl
    {
        public ChamferControlViewModel(XElement options, bool canChangeOptions) : base(options, canChangeOptions) { }

        /// <summary>
        /// Text shown in the control.
        /// </summary>
        private string _text = "Chamfer";
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }
    }
}
