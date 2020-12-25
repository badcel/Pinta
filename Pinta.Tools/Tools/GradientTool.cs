// 
// GradientTool.cs
//  
// Author:
//       Olivier Dufour <olivier.duff@gmail.com>
// 
// Copyright (c) 2010 Olivier Dufour
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using Cairo;
using Pinta.Core;

namespace Pinta.Tools
{
	public class GradientTool : BaseTool
	{
		Cairo.PointD startpoint;
		bool tracking;
		protected ImageSurface? undo_surface;
		uint button;

		public override string Name {
			get { return Translations.GetString ("Gradient"); }
		}

		public override string Icon {
			get { return Resources.Icons.ToolGradient; }
		}

		public override string StatusBarText {
			get { return Translations.GetString ("Click and drag to draw gradient from primary to secondary color.  Right click to reverse."); }
		}
		public override Gdk.Key ShortcutKey { get { return Gdk.Key.G; } }
        public override Gdk.Cursor DefaultCursor { get { return new Gdk.Cursor (Gdk.Display.Default, PintaCore.Resources.GetIcon ("Cursor.Gradient.png"), 9, 18); } }
		public override int Priority { get { return 23; } }

		#region Mouse Handlers
		protected override void OnMouseDown (Gtk.DrawingArea canvas, Gtk.ButtonPressEventArgs args, Cairo.PointD point)
		{
			Document doc = PintaCore.Workspace.ActiveDocument;

			// Protect against history corruption
			if (tracking)
				return;
		
			startpoint = point;
			if (!doc.Workspace.PointInCanvas(point))
				return;

			tracking = true;
			button = args.Event.Button;
			undo_surface = doc.Layers.CurrentUserLayer.Surface.Clone ();
		}

		protected override void OnMouseUp (Gtk.DrawingArea canvas, Gtk.ButtonReleaseEventArgs args, Cairo.PointD point)
		{
			Document doc = PintaCore.Workspace.ActiveDocument;

			if (!tracking || args.Event.Button != button)
				return;
		
			tracking = false;

			if (undo_surface != null)
				doc.History.PushNewItem (new SimpleHistoryItem (Icon, Name, undo_surface, doc.Layers.CurrentUserLayerIndex));
		}

		protected override void OnMouseMove (object o, Gtk.MotionNotifyEventArgs? args, Cairo.PointD point)
		{
			Document doc = PintaCore.Workspace.ActiveDocument;

			if (tracking) {
				GradientRenderer gr = CreateGradientRenderer ();
				
				if (button == 3) {	// Right-click
					gr.StartColor = PintaCore.Palette.SecondaryColor.ToColorBgra ();
					gr.EndColor = PintaCore.Palette.PrimaryColor.ToColorBgra ();
				} else {		//1 Left-click
					gr.StartColor = PintaCore.Palette.PrimaryColor.ToColorBgra ();
					gr.EndColor = PintaCore.Palette.SecondaryColor.ToColorBgra ();
				}
						
				gr.StartPoint = startpoint;
				gr.EndPoint = point;
				gr.AlphaBlending = UseAlphaBlending;
        
				gr.BeforeRender ();

				Gdk.Rectangle selection_bounds = doc.GetSelectedBounds (true);
				ImageSurface scratch_layer = doc.Layers.ToolLayer.Surface;

				gr.Render (scratch_layer, new Gdk.Rectangle[] { selection_bounds });

				using (var g = doc.CreateClippedContext ()) {
					g.SetSource (scratch_layer);
					g.Paint ();
				}

				doc.Layers.ToolLayer.Clear ();

				selection_bounds.Inflate (5, 5);
				doc.Workspace.Invalidate (selection_bounds);
			}
		}

		private GradientRenderer CreateGradientRenderer ()
		{
			var normalBlendOp = new UserBlendOps.NormalBlendOp ();
			bool alpha_only = SelectedGradientColorMode == GradientColorMode.Transparency;

			switch (SelectedGradientType) {
				case GradientType.Linear:
					return new GradientRenderers.LinearClamped (alpha_only, normalBlendOp);
				case GradientType.LinearReflected:
					return new GradientRenderers.LinearReflected (alpha_only, normalBlendOp);
				case GradientType.Radial:
					return new GradientRenderers.Radial (alpha_only, normalBlendOp);
				case GradientType.Diamond:
					return new GradientRenderers.LinearDiamond (alpha_only, normalBlendOp);
				case GradientType.Conical:
					return new GradientRenderers.Conical (alpha_only, normalBlendOp);
			}

			throw new ArgumentOutOfRangeException ("Unknown gradient type.");
		}
		#endregion

		#region ToolBar
		// NRT - Created in OnBuildToolBar
		private ToolBarLabel gradient_label = null!;
		private ToolBarDropDownButton gradient_button = null!;
		//private ToolBarLabel mode_label;
		//private ToolBarDropDownButton mode_button;
		
		protected override void OnBuildToolBar (Gtk.Toolbar tb)
		{
			base.OnBuildToolBar (tb);

			if (gradient_label == null)
				gradient_label = new ToolBarLabel (string.Format (" {0}: ", Translations.GetString ("Gradient")));

			tb.AppendItem (gradient_label);

			if (gradient_button == null) {
				gradient_button = new ToolBarDropDownButton ();

				gradient_button.AddItem (Translations.GetString ("Linear Gradient"), Resources.Icons.GradientLinear, GradientType.Linear);
				gradient_button.AddItem (Translations.GetString ("Linear Reflected Gradient"), Resources.Icons.GradientLinearReflected, GradientType.LinearReflected);
				gradient_button.AddItem (Translations.GetString ("Linear Diamond Gradient"), Resources.Icons.GradientDiamond, GradientType.Diamond);
				gradient_button.AddItem (Translations.GetString ("Radial Gradient"), Resources.Icons.GradientRadial, GradientType.Radial);
				gradient_button.AddItem (Translations.GetString ("Conical Gradient"), Resources.Icons.GradientConical, GradientType.Conical);
			}

			tb.AppendItem (gradient_button);

			// Hide TransparentMode.  The core issue is we can't just paint it on top of the
			// current layer because it's transparent.  Will require significant effort to support.

			//tb.AppendItem (new Gtk.SeparatorToolItem ());

			//if (mode_label == null)
			//        mode_label = new ToolBarLabel (string.Format (" {0}: ", Translations.GetString ("Mode")));

			//tb.AppendItem (mode_label);

			//if (mode_button == null) {
			//        mode_button = new ToolBarDropDownButton ();

			//        mode_button.AddItem (Translations.GetString ("Color Mode"), Resources.Icons.ColorModeColor, GradientColorMode.Color);
			//        mode_button.AddItem (Translations.GetString ("Transparency Mode"), Resources.Icons.ColorModeTransparency, GradientColorMode.Transparency);
			//}

			//tb.AppendItem (mode_button);
		}

		private GradientType SelectedGradientType {
			get { return gradient_button.SelectedItem.GetTagOrDefault (GradientType.Linear); }
		}

		private GradientColorMode SelectedGradientColorMode {
			// get { return (GradientColorMode)mode_button.SelectedItem.Tag; }
			get { return GradientColorMode.Color; }
		}
		#endregion

		enum GradientType
		{
			Linear,
			LinearReflected,
			Diamond,
			Radial,
			Conical
		}
	}
}
