// 
// MainWindow.cs
//  
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
// 
// Copyright (c) 2010 Jonathan Pobst
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
using System.Collections.Generic;
using System.Linq;
using Gtk;
using Pinta.Core;
using Pinta.Docking;
using Pinta.Gui.Widgets;
using Pinta.MacInterop;

namespace Pinta
{
	public class MainWindow
	{
		Adw.Application app;
		// NRT - Created in OnActivated
		WindowShell window_shell = null!;
		Dock dock = null!;
		Gio.Menu show_pad = null!;

		CanvasPad canvas_pad = null!;

		private readonly System.Net.Http.HttpClient http_client = new ();

		public MainWindow (Adw.Application app)
		{
			this.app = app;

			// This needs to match the name of the .desktop file in order to
			// show the correct application icon under some environments (e.g.
			// KDE Wayland). See bug 1967687.
			GLib.Functions.SetPrgname ("pinta");
		}

		public void Activate ()
		{
			// Build our window
			CreateWindow ();

			// Initialize interface things
			new ActionHandlers ();

			PintaCore.Chrome.InitializeProgessDialog (new ProgressDialog ());
#if false // TODO-GTK4
			PintaCore.Chrome.InitializeErrorDialogHandler ((parent, message, details) => {
				System.Console.Error.WriteLine ("Pinta: {0}", details);
				using var errorDialog = new ErrorDialog (parent);
				errorDialog.SetMessage (message);
				errorDialog.AddDetails (details);

				while (true) {
					var response = (ResponseType) errorDialog.Run ();
					if (response != ResponseType.Help)
						break;
				}
			});
#endif

			PintaCore.Chrome.InitializeMessageDialog (ShowMessageDialog);

			PintaCore.Initialize ();

			// Initialize extensions
			// TODO-GTK3 (addins)
#if false
			AddinManager.Initialize ();
			AddinManager.Registry.Update ();
			AddinSetupService setupService = new AddinSetupService (AddinManager.Registry);
			if (!setupService.AreRepositoriesRegistered ())
				setupService.RegisterRepositories (true);

			//Look out for any changes in extensions
			AddinManager.AddExtensionNodeHandler (typeof (IExtension), OnExtensionChanged);
#else
			var tools = new Pinta.Tools.CoreToolsExtension ();
			tools.Initialize ();
			var effects = new Pinta.Effects.CoreEffectsExtension ();
			effects.Initialize ();
#endif

			// Load the user's previous settings
			LoadUserSettings ();
			PintaCore.Actions.App.BeforeQuit += delegate { SaveUserSettings (); };

#if false // TODO-GTK4
			// We support drag and drop for URIs
			window_shell.AddDragDropSupport (new Gtk.TargetEntry ("text/uri-list", 0, 100));

			// Handle a few main window specific actions
			window_shell.DragDataReceived += MainWindow_DragDataReceived;
#endif
			window_shell.Window.OnCloseRequest += HandleCloseRequest;

			var key_controller = Gtk.EventControllerKey.New ();
			key_controller.OnKeyPressed += HandleGlobalKeyPress;
			key_controller.OnKeyReleased += HandleGlobalKeyRelease;
			window_shell.Window.AddController (key_controller);

			// TODO: These need to be [re]moved when we redo zoom support
			PintaCore.Actions.View.ZoomToWindow.Activated += ZoomToWindow_Activated;
			PintaCore.Actions.View.ZoomToSelection.Activated += ZoomToSelection_Activated;

			PintaCore.Workspace.ActiveDocumentChanged += ActiveDocumentChanged;

			PintaCore.Workspace.DocumentCreated += Workspace_DocumentCreated;
			PintaCore.Workspace.DocumentClosed += Workspace_DocumentClosed;

			var notebook = canvas_pad.Notebook;
			notebook.TabClosed += DockNotebook_TabClosed;
			notebook.ActiveTabChanged += DockNotebook_ActiveTabChanged;
		}

		private void Workspace_DocumentClosed (object? sender, DocumentEventArgs e)
		{
			var tab = FindTabWithCanvas ((PintaCanvas) e.Document.Workspace.Canvas);

			if (tab != null)
				canvas_pad.Notebook.RemoveTab (tab);
		}

		private void DockNotebook_TabClosed (object? sender, TabClosedEventArgs e)
		{
			var view = (DocumentViewContent) e.Item;

			if (PintaCore.Workspace.OpenDocuments.IndexOf (view.Document) > -1) {
				PintaCore.Workspace.SetActiveDocument (view.Document);
				PintaCore.Actions.File.Close.Activate ();

				// User must have canceled the close
				if (PintaCore.Workspace.OpenDocuments.IndexOf (view.Document) > -1)
					e.Cancel = true;
			}
		}

		private void DockNotebook_ActiveTabChanged (object? sender, EventArgs e)
		{
			var item = canvas_pad.Notebook.ActiveItem;

			if (item == null)
				return;

			var view = (DocumentViewContent) item;

			PintaCore.Workspace.SetActiveDocument (view.Document);
			((CanvasWindow) view.Widget).Canvas.Cursor = PintaCore.Tools.CurrentTool?.CurrentCursor;
		}

		private void Workspace_DocumentCreated (object? sender, DocumentEventArgs e)
		{
			var doc = e.Document;

			var notebook = canvas_pad.Notebook;
			var selected_index = notebook.ActiveItemIndex;

			var canvas = new CanvasWindow (doc) {
				RulersVisible = PintaCore.Actions.View.Rulers.Value,
				RulerMetric = GetCurrentRulerMetric ()
			};
			doc.Workspace.Canvas = canvas.Canvas;

			var my_content = new DocumentViewContent (doc, canvas);

			// Insert our tab to the right of the currently selected tab
			notebook.InsertTab (my_content, selected_index + 1);

			// Zoom to window only on first show (if we do it always, it will be called on every resize)
			// Note: this does seem to allow a small flicker where large images are shown at 100% zoom before
			// zooming out (Bug 1959673)
			canvas.Canvas.OnResize += (o, e2) => {
				if (!canvas.HasBeenShown) {
					GLib.Functions.TimeoutAddFull (0, 0, delegate {
						ZoomToWindow_Activated (o, e);
						PintaCore.Workspace.Invalidate ();
						return false;
					});
				}

				canvas.HasBeenShown = true;
			};

			PintaCore.Actions.View.Rulers.Toggled += (active) => { canvas.RulersVisible = active; };
			PintaCore.Actions.View.RulerMetric.OnActivate += (o, args) => {
				PintaCore.Actions.View.RulerMetric.ChangeState (args.Parameter!);
				canvas.RulerMetric = GetCurrentRulerMetric ();
			};
		}

		private MetricType GetCurrentRulerMetric ()
		{
			return (MetricType) PintaCore.Actions.View.RulerMetric.GetState ().GetInt ();
		}

		private bool HandleGlobalKeyPress (Gtk.EventControllerKey controller, Gtk.EventControllerKey.KeyPressedSignalArgs args)
		{
			// Give the widget that has focus a first shot at handling the event.
			// Otherwise, key presses may be intercepted by shortcuts for menu items.
			if (SendToFocusWidget (controller))
				return true;

			// Give the Canvas (and by extension the tools)
			// first shot at handling the event if
			// the mouse pointer is on the canvas
			if (PintaCore.Workspace.HasOpenDocuments) {
				var canvas_window = ((PintaCanvas) PintaCore.Workspace.ActiveWorkspace.Canvas).CanvasWindow;

				if ((canvas_window.Canvas.HasFocus || canvas_window.IsMouseOnCanvas) &&
				     canvas_window.Canvas.DoKeyPressEvent (controller, args)) {
					return true;
				}
			}

			// If the canvas/tool didn't consume it, see if its a toolbox shortcut
			if (!args.State.HasModifierKey () && PintaCore.Tools.SetCurrentTool (args.GetKey ())) {
				return true;
			}

			// Finally, see if the palette widget wants it.
			return PintaCore.Palette.DoKeyPress (args);
		}

		private void HandleGlobalKeyRelease (Gtk.EventControllerKey controller, Gtk.EventControllerKey.KeyReleasedSignalArgs args)
		{
			if (SendToFocusWidget (controller) || !PintaCore.Workspace.HasOpenDocuments)
				return;

			// Give the Canvas (and by extension the tools)
			// first shot at handling the event if
			// the mouse pointer is on the canvas
			var canvas_window = ((PintaCanvas) PintaCore.Workspace.ActiveWorkspace.Canvas).CanvasWindow;

			if (canvas_window.Canvas.HasFocus || canvas_window.IsMouseOnCanvas)
				canvas_window.Canvas.DoKeyReleaseEvent (controller, args);
		}

		private bool SendToFocusWidget (Gtk.EventControllerKey key_controller)
		{
			var widget = window_shell.Window.FocusWidget;
			if (widget != null && key_controller.Forward (widget)) {
				return true;
			}

			return false;
		}

		// Called when an extension node is added or removed
		// TODO-GTK3 (addins)
#if false
		private void OnExtensionChanged (object s, ExtensionNodeEventArgs args)
		{
			IExtension extension = (IExtension) args.ExtensionObject;
			if (args.Change == ExtensionChange.Add)
				extension.Initialize ();
			else
				extension.Uninitialize ();
		}
#endif

		#region GUI Construction
		private void CreateWindow ()
		{
			// Check for stored window settings
			var width = PintaCore.Settings.GetSetting<int> ("window-size-width", 1100);
			var height = PintaCore.Settings.GetSetting<int> ("window-size-height", 750);
			var maximize = PintaCore.Settings.GetSetting<bool> ("window-maximized", false);

			window_shell = new WindowShell (app, "Pinta.GenericWindow", "Pinta", width, height, maximize);

			CreateMainMenu (window_shell);
			CreateMainToolBar (window_shell);
			CreateToolToolBar (window_shell);

			CreatePanels (window_shell);
			CreateStatusBar (window_shell);

			app.AddWindow (window_shell.Window);

			PintaCore.Chrome.InitializeApplication (app);
			PintaCore.Chrome.InitializeWindowShell (window_shell.Window);
		}
		#endregion

		private void CreateMainMenu (WindowShell shell)
		{
			var menu_bar = Gio.Menu.New ();

			if (window_shell.HeaderBar is null) {
				app.Menubar = menu_bar;

				if (PintaCore.System.OperatingSystem == OS.Mac) {
					// Only use the application menu on macOS. On other platforms, these
					// menu items appear under File, Help, etc.
					// The first menu seems to be treated as the application menu.
					var app_menu = Gio.Menu.New ();
					PintaCore.Actions.App.RegisterActions (app, app_menu);
					menu_bar.AppendSubmenu ("_Application", app_menu);
				}
			} else {
				var header_bar = window_shell.HeaderBar;
				header_bar.PackEnd (new Gtk.MenuButton () {
					MenuModel = menu_bar,
					IconName = Resources.StandardIcons.OpenMenu
				});
			}

			var file_menu = Gio.Menu.New ();
			PintaCore.Actions.File.RegisterActions (app, file_menu);
			menu_bar.AppendSubmenu (Translations.GetString ("_File"), file_menu);

			var edit_menu = Gio.Menu.New ();
			PintaCore.Actions.Edit.RegisterActions (app, edit_menu);
			menu_bar.AppendSubmenu (Translations.GetString ("_Edit"), edit_menu);

			var view_menu = Gio.Menu.New ();
			PintaCore.Actions.View.RegisterActions (app, view_menu);
			menu_bar.AppendSubmenu (Translations.GetString ("_View"), view_menu);

			var image_menu = Gio.Menu.New ();
			PintaCore.Actions.Image.RegisterActions (app, image_menu);
			menu_bar.AppendSubmenu (Translations.GetString ("_Image"), image_menu);

			var layers_menu = Gio.Menu.New ();
			PintaCore.Actions.Layers.RegisterActions (app, layers_menu);
			menu_bar.AppendSubmenu (Translations.GetString ("_Layers"), layers_menu);

			var adj_menu = Gio.Menu.New ();
			menu_bar.AppendSubmenu (Translations.GetString ("_Adjustments"), adj_menu);

			var effects_menu = Gio.Menu.New ();
			menu_bar.AppendSubmenu (Translations.GetString ("Effe_cts"), effects_menu);

			// TODO-GTK3 (addins)
			// Addins are disabled until Mono.Addins (or a replacement) is ported to .NET 6 / GTK3
#if false
			var addins_menu = new GLib.Menu ();
			PintaCore.Actions.Addins.RegisterActions (this, addins_menu);
			menu_bar.AppendSubmenu (Translations.GetString ("A_dd-ins"), addins_menu);
#endif

			var window_menu = Gio.Menu.New ();
			PintaCore.Actions.Window.RegisterActions (app, window_menu);
			menu_bar.AppendSubmenu (Translations.GetString ("_Window"), window_menu);

			var help_menu = Gio.Menu.New ();
			PintaCore.Actions.Help.RegisterActions (app, help_menu);
			menu_bar.AppendSubmenu (Translations.GetString ("_Help"), help_menu);

			var pad_section = Gio.Menu.New ();
			view_menu.AppendSection (null, pad_section);

			show_pad = Gio.Menu.New ();
			pad_section.AppendSubmenu (Translations.GetString ("Tool Windows"), show_pad);

			PintaCore.Chrome.InitializeMainMenu (adj_menu, effects_menu);
		}

		private void CreateMainToolBar (WindowShell shell)
		{
			var main_toolbar = window_shell.CreateToolBar ("main_toolbar");

			if (window_shell.HeaderBar is not null)
				PintaCore.Actions.CreateHeaderToolBar (window_shell.HeaderBar!);
			else
				PintaCore.Actions.CreateToolBar (main_toolbar);

			PintaCore.Chrome.InitializeMainToolBar (main_toolbar);
		}

		private void CreateToolToolBar (WindowShell shell)
		{
			var tool_toolbar = window_shell.CreateToolBar ("tool_toolbar");
			tool_toolbar.HeightRequest = 42;

			PintaCore.Chrome.InitializeToolToolBar (tool_toolbar);
		}

		private void CreateStatusBar (WindowShell shell)
		{
			var statusbar = shell.CreateStatusBar ("statusbar");

			statusbar.Append (new StatusBarColorPaletteWidget () {
				Hexpand = true,
				Halign = Align.Fill
			});

			PintaCore.Actions.CreateStatusBar (statusbar);

			PintaCore.Chrome.InitializeStatusBar (statusbar);
		}

		private void CreatePanels (WindowShell shell)
		{
			Box panel_container = shell.CreateWorkspace ();
			CreateDockAndPads (panel_container);
		}

		private void CreateDockAndPads (Box container)
		{
			var toolbox = new ToolBoxWidget ();
			var toolbox_scroll = new ScrolledWindow () {
				Child = toolbox,
				HscrollbarPolicy = PolicyType.Never,
				VscrollbarPolicy = PolicyType.Automatic,
				HasFrame = false,
				OverlayScrolling = true,
				WindowPlacement = CornerType.BottomRight
			};
			container.Append (toolbox_scroll);
			PintaCore.Chrome.InitializeToolBox (toolbox);

			// Dock widget
			dock = new Dock ();
			dock.Hexpand = true;
			dock.Halign = Align.Fill;

			// Canvas pad
			canvas_pad = new CanvasPad ();
			canvas_pad.Initialize (dock);
			PintaCore.Chrome.InitializeImageTabsNotebook (canvas_pad.Notebook);
#if false // TODO-GTK4

			// Layer pad
			var layers_pad = new LayersPad ();
			layers_pad.Initialize (dock, this, show_pad);
#endif

			// History pad
			var history_pad = new HistoryPad ();
			history_pad.Initialize (dock, app, show_pad);

			container.Append (dock);
		}

		#region User Settings
		private const string LastDialogDirSettingKey = "last-dialog-directory";
		private const string LastSelectedToolSettingKey = "last-selected-tool";

		private void LoadUserSettings ()
		{
			dock.LoadSettings (PintaCore.Settings);

			// Set selected tool to last selected or default to the PaintBrush
			PintaCore.Tools.SetCurrentTool (PintaCore.Settings.GetSetting (LastSelectedToolSettingKey, "PaintBrushTool"));

			PintaCore.Actions.View.Rulers.Value = PintaCore.Settings.GetSetting ("ruler-shown", false);
			PintaCore.Actions.View.ToolBar.Value = PintaCore.Settings.GetSetting ("toolbar-shown", true);
			PintaCore.Actions.View.StatusBar.Value = PintaCore.Settings.GetSetting ("statusbar-shown", true);
			PintaCore.Actions.View.ToolBox.Value = PintaCore.Settings.GetSetting ("toolbox-shown", true);
			PintaCore.Actions.View.ImageTabs.Value = PintaCore.Settings.GetSetting ("image-tabs-shown", true);
			PintaCore.Actions.View.PixelGrid.Value = PintaCore.Settings.GetSetting ("pixel-grid-shown", false);

			var dialog_uri = PintaCore.Settings.GetSetting (LastDialogDirSettingKey, PintaCore.RecentFiles.DefaultDialogDirectory?.GetUri () ?? "");
			PintaCore.RecentFiles.LastDialogDirectory = Gio.FileHelper.NewForUri (dialog_uri);

			var ruler_metric = (MetricType) PintaCore.Settings.GetSetting ("ruler-metric", (int) MetricType.Pixels);
			PintaCore.Actions.View.RulerMetric.Activate (GLib.Variant.Create ((int) ruler_metric));
		}

		private void SaveUserSettings ()
		{
			dock.SaveSettings (PintaCore.Settings);

			// Don't store the maximized height if the window is maximized
			if (!window_shell.Window.IsMaximized ()) {
				PintaCore.Settings.PutSetting ("window-size-width", window_shell.Window.GetWidth ());
				PintaCore.Settings.PutSetting ("window-size-height", window_shell.Window.GetHeight ());
			}

			PintaCore.Settings.PutSetting ("ruler-metric", (int) GetCurrentRulerMetric ());
			PintaCore.Settings.PutSetting ("window-maximized", window_shell.Window.IsMaximized ());
			PintaCore.Settings.PutSetting ("ruler-shown", PintaCore.Actions.View.Rulers.Value);
			PintaCore.Settings.PutSetting ("image-tabs-shown", PintaCore.Actions.View.ImageTabs.Value);
			PintaCore.Settings.PutSetting ("toolbar-shown", PintaCore.Actions.View.ToolBar.Value);
			PintaCore.Settings.PutSetting ("statusbar-shown", PintaCore.Actions.View.StatusBar.Value);
			PintaCore.Settings.PutSetting ("toolbox-shown", PintaCore.Actions.View.ToolBox.Value);
			PintaCore.Settings.PutSetting ("pixel-grid-shown", PintaCore.Actions.View.PixelGrid.Value);
			PintaCore.Settings.PutSetting (LastDialogDirSettingKey, PintaCore.RecentFiles.LastDialogDirectory?.GetUri () ?? "");

			if (PintaCore.Tools.CurrentTool is BaseTool tool)
				PintaCore.Settings.PutSetting (LastSelectedToolSettingKey, tool.GetType ().Name);

			PintaCore.Settings.DoSaveSettingsBeforeQuit ();
		}
		#endregion

		#region Action Handlers
		private bool HandleCloseRequest (object o, EventArgs args)
		{
			PintaCore.Actions.App.Exit.Activate ();

			// Stop the default handler from running so the user can cancel quitting.
			return true;
		}

#if false // TODO-GTK4
		private async void MainWindow_DragDataReceived (object o, DragDataReceivedArgs args)
		{
			// TODO: Generate random name for the picture being downloaded

			// Only handle URIs
			if (args.Info != 100)
				return;

			string fullData = System.Text.Encoding.UTF8.GetString (args.SelectionData.Data).TrimEnd ('\0');

			foreach (string individualFile in fullData.Split (System.Environment.NewLine)) {
				string file = individualFile.Trim ();
				if (string.IsNullOrEmpty (file))
					continue;

				if (file.StartsWith ("http") || file.StartsWith ("ftp")) {
					string tempFilePath = System.IO.Path.GetTempPath () + System.IO.Path.GetFileName (file);

					var progressDialog = PintaCore.Chrome.ProgressDialog;

					try {
						PintaCore.Chrome.MainWindowBusy = true;

						progressDialog.Title = Translations.GetString ("Downloading Image");
						progressDialog.Text = "";
						progressDialog.Show ();

						{
							using var response = await http_client.GetAsync (file, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
							using var contentStream = response.Content.ReadAsStream ();
							using var fileStream = System.IO.File.Create (tempFilePath);
							contentStream.CopyTo (fileStream);
						}

						if (PintaCore.Workspace.OpenFile (GLib.FileFactory.NewForPath (tempFilePath))) {
							// Mark as not having a file, so that the user doesn't unintentionally
							// save using the temp file.
							PintaCore.Workspace.ActiveDocument.ClearFileReference ();
						}
					} catch (Exception e) {
						progressDialog.Hide ();
						PintaCore.Chrome.ShowErrorDialog (PintaCore.Chrome.MainWindow,
							Translations.GetString ("Download failed"),
							string.Format (Translations.GetString ("Unable to download image from {0}.\nDetails: {1}"), file, e.Message));
					} finally {
						progressDialog.Hide ();
						PintaCore.Chrome.MainWindowBusy = false;
					}
				} else {
					PintaCore.Workspace.OpenFile (Core.GtkExtensions.FileNewForCommandlineArg (file));
				}
			}
		}
#endif

		private void ZoomToSelection_Activated (object sender, EventArgs e)
		{
			PintaCore.Workspace.ActiveWorkspace.ZoomToRectangle (PintaCore.Workspace.ActiveDocument.Selection.SelectionPath.GetBounds ().ToDouble ());
		}

		private void ZoomToWindow_Activated (object sender, EventArgs e)
		{
			// The image is small enough to fit in the window
			if (PintaCore.Workspace.ImageFitsInWindow) {
				PintaCore.Actions.View.ActualSize.Activate ();
			} else {
				int image_x = PintaCore.Workspace.ImageSize.Width;
				int image_y = PintaCore.Workspace.ImageSize.Height;

				var canvas_window = PintaCore.Workspace.ActiveWorkspace.Canvas.Parent!;

				var window_x = canvas_window.GetAllocatedWidth ();
				var window_y = canvas_window.GetAllocatedHeight ();

				double ratio;

				// The image is more constrained by width than height
				if ((double) image_x / (double) window_x >= (double) image_y / (double) window_y) {
					ratio = (double) (window_x - 20) / (double) image_x;
				} else {
					ratio = (double) (window_y - 20) / (double) image_y;
				}

				PintaCore.Workspace.Scale = ratio;
				PintaCore.Actions.View.SuspendZoomUpdate ();
				PintaCore.Actions.View.ZoomComboBox.ComboBox.GetEntry ().SetText (ViewActions.ToPercent (PintaCore.Workspace.Scale));
				PintaCore.Actions.View.ResumeZoomUpdate ();
			}

			PintaCore.Actions.View.ZoomToWindowActivated = true;
		}
		#endregion


		private void ActiveDocumentChanged (object? sender, EventArgs e)
		{
			if (PintaCore.Workspace.HasOpenDocuments) {
				PintaCore.Actions.View.SuspendZoomUpdate ();
				PintaCore.Actions.View.ZoomComboBox.ComboBox.GetEntry ().SetText (ViewActions.ToPercent (PintaCore.Workspace.Scale));
				PintaCore.Actions.View.ResumeZoomUpdate ();

				var doc = PintaCore.Workspace.ActiveDocument;
				var tab = FindTabWithCanvas ((PintaCanvas) doc.Workspace.Canvas);

				if (tab != null) {
					canvas_pad.Notebook.ActiveItem = tab;
				}

				doc.Workspace.Canvas.GrabFocus ();
			}
		}

		private IDockNotebookItem? FindTabWithCanvas (PintaCanvas canvas)
		{
			return canvas_pad.Notebook.Items
				.Where (i => ((CanvasWindow) i.Widget).Canvas == canvas)
				.FirstOrDefault ();
		}

		private static void ShowMessageDialog (Window parent, string message, string details)
		{
			System.Console.Error.WriteLine ("Pinta: {0}\n{1}", message, details);

			var dialog = Adw.MessageDialog.New (parent, message, details);

			const string ok_response = "ok";
			dialog.AddResponse (ok_response, Translations.GetString ("_OK"));
			dialog.DefaultResponse = ok_response;
			dialog.CloseResponse = ok_response;

			dialog.Present ();
		}
	}
}
