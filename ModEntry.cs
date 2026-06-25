using ForageTrackerModSV;
using ForageTrackerModSV.Debug;
using LightRadiusMod;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace ForageTrackerMod
{
    /// <summary>
    /// Entry point for the Forage Tracker mod.
    ///
    /// Enabled state is managed structurally: when the mod is active, the tracking
    /// event handlers are subscribed; when inactive they are unsubscribed entirely.
    /// This means zero overhead when disabled — no handlers exist in the call chain,
    /// so there is nothing to check or branch on per-event.
    ///
    ///   Disabled ──(Enable)──► Active
    ///   Active   ──(Disable)─► Disabled
    ///
    /// Transitions happen via <see cref="SetEnabled"/> which subscribes or
    /// unsubscribes the appropriate handlers atomically.
    /// </summary>
    public sealed class ModEntry : Mod
    {
        // ── Fields ───────────────────────────────────────────────────────────────

        private ModConfig _config = null!;
        private ForageTracker _tracker = null!;
        private MapRegionConfig _regionConfig = null!;
        /// <summary>True while the day-tracking event handlers are subscribed.</summary>
        private bool _active = false;

        private ClickableTextureComponent? _editRegionsButton;
        private bool _configMenuOpen;
        private bool _editButtonWasDown;

        // ── Entry ────────────────────────────────────────────────────────────────

        public override void Entry(IModHelper helper)
        {
            _config = helper.ReadConfig<ModConfig>();
            _tracker = new ForageTracker(Monitor);

            LocationHierarchy.Init(Monitor);
            // Load region data — creates regions.json with defaults on first run
            _regionConfig = helper.Data.ReadJsonFile<MapRegionConfig>("regions.json")
                            ?? new MapRegionConfig();
            helper.Data.WriteJsonFile("regions.json", _regionConfig);

            // Inject dependencies into the drawer
            MapTooltipDrawer.Tracker = _tracker;
            MapTooltipDrawer.Config = _config;
            MapTooltipDrawer.Monitor = Monitor;
            MapTooltipDrawer.SetRegionsByMap(_regionConfig.RegionsByMap);
            MapTooltipDrawer.SetBindings(_regionConfig.Bindings);

            // The drawer calls this when the player clicks "Edit Regions"
           // MapTooltipDrawer.OpenEditorAction = OpenEditor;

            // Always-on events
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.World.DebrisListChanged += OnDebrisListChanged; 
            SetEnabled(_config.Enabled);
            Debugger.DebugLog(Monitor, $"Forage Tracker loaded (initially {(_config.Enabled ? "enabled" : "disabled")}).", LogLevel.Info);
        }

        private readonly Dictionary<Debris, Vector2> _trackedDebris = new();
        private void OnDebrisListChanged(object? sender, DebrisListChangedEventArgs e)
        {
            foreach (var debris in e.Added)
            {
                // debris.item is the actual dropped item (if any)
                if (debris.item is not StardewValley.Object obj)  continue;

                // only forageables
                if (!obj.isForage()) continue;

                var location = e.Location;
               
                Monitor.Log(
                    $"[ForageTracker] Debris forage added: {obj.DisplayName} " +
                    $"@ ({debris.Chunks?[0]?.position ?? null}) in {location.Name}",
                    LogLevel.Debug
                );
                Monitor.Log(
    $"Added debris hash={debris.GetHashCode()}",
    LogLevel.Debug);

                Vector2 tile = obj.TileLocation;
                _trackedDebris[debris] = tile;

                _tracker.MarkAdded(e.Location.Name, tile, obj.QualifiedItemId, obj.DisplayName);

                // Rebuild the location key cache in case this location
                // wasn't tracked before (e.g. first forageable of the day here).
                MapTooltipDrawer.RebuildLocationKeyCache();
                // Positioner box size will change if line count changes —
                // invalidate so it re-solves on the next hover frame.
                TooltipPositioner.Invalidate();
            }
            // REMOVE (THIS IS WHAT YOU’RE MISSING)
            foreach (var debris in e.Removed) // To catch foragables that were picked up and dropped -thus turning into debrie.
            {

                if (!_trackedDebris.TryGetValue(debris, out Vector2 tile))
                    continue;

                _trackedDebris.Remove(debris);

                _tracker.MarkPicked(e.Location.Name, tile);

                Monitor.Log(
                    $"Removed tracked debris at {tile}",
                    LogLevel.Debug );

                // Rebuild the location key cache in case this location
                // wasn't tracked before (e.g. first forageable of the day here).
                MapTooltipDrawer.RebuildLocationKeyCache();
                // Positioner box size will change if line count changes —
                // invalidate so it re-solves on the next hover frame.
                TooltipPositioner.Invalidate();

            }
           

        }
        // ── Enabled state — subscribe/unsubscribe pattern ────────────────────────

        private void SetEnabled(bool enable)
        {
            if (enable == _active) return;

            if (enable)
            {
                Helper.Events.GameLoop.DayStarted += OnDayStarted;
                Helper.Events.World.ObjectListChanged += OnObjectListChanged;
                Debugger.DebugLog(Monitor, "[ForageTracker] Tracking activated.", LogLevel.Debug);

            }
            else
            {
                Helper.Events.GameLoop.DayStarted -= OnDayStarted;
                Helper.Events.World.ObjectListChanged -= OnObjectListChanged;
                Debugger.DebugLog(Monitor, "[ForageTracker] Tracking deactivated.", LogLevel.Debug);
            }

            _active = enable;
        }

        // ── Always-on handlers ───────────────────────────────────────────────────

        private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
            => MapTooltipDrawer.OnRenderedActiveMenu(e.SpriteBatch);

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            if (!_active) return;
            _tracker.ScanAllLocations();
            LocationHierarchy.Rebuild();
            MapTooltipDrawer.RebuildLocationKeyCache();
        }
        /// <summary>
        /// Handles mouse left-click to detect a click on the "✏ Edit Regions"
        /// button drawn by the drawer over the map page.
        /// No keybind — the button is the only way to open the editor.
        /// </summary>
        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            /*
            if (!Context.IsWorldReady) return;
            if (e.Button != SButton.MouseLeft) return;

            var cursor = e.Cursor.ScreenPixels;
            if (MapTooltipDrawer.OnMapLeftClick((int)cursor.X, (int)cursor.Y))
                Helper.Input.Suppress(e.Button);*/

            if (!Context.IsWorldReady)  return;

            // =====================================================
            // TEXT INPUT FOR MAP EDITOR
            // =====================================================
            if (Game1.activeClickableMenu is MapRegionEditor editor)
            {
                if (e.Button.TryGetKeyboard(out Keys key))
                {
                    char c = GetCharFromKey(key);

                    if (c != '\0')
                    {
                        editor.ReceiveTextInput(c);
                        Helper.Input.Suppress(e.Button);
                    }
                }
            }
        }

        // ── Tracking handlers (only subscribed while active) ─────────────────────

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            LocationHierarchy.Rebuild();
            _tracker.ScanAllLocations();
            MapTooltipDrawer.RebuildLocationKeyCache();
        }

        private void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
        {
            // ── Item picked up ────────────────────────────────────────────────
            foreach (var (tile, obj) in e.Removed)
            {
                // Outdoor spawned items: IsSpawnedObject = true (day-start scan).
                // Indoor re-dropped items: IsSpawnedObject = false, but their
                // QualifiedItemId is in _knownForageableIds from the day scan.
                bool isTrackedSpawn   = obj.IsSpawnedObject;
                bool isTrackedDropped = _tracker.IsKnownForageableId(obj.QualifiedItemId);

                if (!isTrackedSpawn && !isTrackedDropped) continue;

                _tracker.MarkPicked(e.Location.Name, tile);
                Debugger.DebugLog(Monitor,
                    $"[ForageTracker] Picked: {obj.DisplayName} " +
                    $"(spawned={obj.IsSpawnedObject}) @ {tile} in {e.Location.Name}",
                    LogLevel.Debug);
                MapTooltipDrawer.RebuildLocationKeyCache();
                TooltipPositioner.Invalidate();
            }

            // ── Item dropped ──────────────────────────────────────────────────
            // When a player drops a forageable it lands as an Object via
            // ObjectListChanged.Added. IsSpawnedObject is false on dropped items,
            // so we identify forageables by QualifiedItemId against the set of
            // IDs seen during the day-start scan.
            //
            // Outdoors: debris physics apply — the item becomes Debris mid-flight
            // and lands as an Object. OnDebrisListChanged handles outdoors.
            // Indoors:  no debris physics — the item goes directly into the
            // location's object list. We handle it here.
            //
            // LocationHierarchy.IsIndoor covers all buildings including modded
            // ones — no hardcoded location names needed.

            var kind = LocationHierarchy.GetKindFast(e.Location.Name);

            if ((int)kind >= (int)LocationType.Outdoor)
            {
                foreach (var (tile, obj) in e.Added)
                {
                    if (!_tracker.IsKnownForageableId(obj.QualifiedItemId)) continue;
                    if (obj.IsSpawnedObject) continue; // day-start item, already tracked

                    Debugger.DebugLog(Monitor,
                        $"[ForageTracker] Indoor drop: {obj.DisplayName} " +
                        $"@ {tile} in {e.Location.Name}",
                        LogLevel.Debug);

                    _tracker.MarkAdded(e.Location.Name, tile,
                        obj.QualifiedItemId, obj.DisplayName);
                    MapTooltipDrawer.RebuildLocationKeyCache();
                    TooltipPositioner.Invalidate();
                }
            }
        }

        // ── Editor ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Opens the MapRegionEditor.
        /// Is called when the player clicks the on-map button.
        /// </summary>
        private bool _pendingOpenEditor = false;

        private void OpenEditor()
        {
            if (!Context.IsWorldReady) return;

            // If we already have a live rect, open immediately.
            if (MapTooltipDrawer.LastLiveMapRect.HasValue)
            {
                OpenEditorNow();
                return;
            }

            // Otherwise: open the GameMenu on the map tab for one render frame so
            // OnRenderedActiveMenu fires and captures LastLiveMapRect, then swap
            // to the editor on the next frame via UpdateTicked.
            Game1.activeClickableMenu?.exitThisMenu();
            var gm = new GameMenu(playOpeningSound: false);
            gm.changeTab(GameMenu.mapTab, playSound: false);
            Game1.activeClickableMenu = gm;
            _pendingOpenEditor = true;
            Helper.Events.GameLoop.UpdateTicked += OnUpdateTickedOpenEditor;
        }

        private void OnUpdateTickedOpenEditor(object? sender, StardewModdingAPI.Events.UpdateTickedEventArgs e)
        {
            // Wait until at least one RenderedActiveMenu fired (rect captured).
            if (!MapTooltipDrawer.LastLiveMapRect.HasValue) return;

            Helper.Events.GameLoop.UpdateTicked -= OnUpdateTickedOpenEditor;
            _pendingOpenEditor = false;

            // Close the temporary GameMenu and open the editor.
            Game1.activeClickableMenu?.exitThisMenu();
            OpenEditorNow();
        }

        void OpenEditorNow()
        {
            Game1.activeClickableMenu = new MapRegionEditor(
                Helper,
                Monitor,
                _regionConfig,
                saved =>
                {
                    _regionConfig = saved;
                    Helper.Data.WriteJsonFile("regions.json", saved);
                    MapTooltipDrawer.SetRegionsByMap(saved.RegionsByMap);
                    MapTooltipDrawer.SetBindings(saved.Bindings);
                    Debugger.DebugLog(Monitor, "[ForageTracker] Regions saved.", LogLevel.Info);
                });
        }

        // ── GMCM registration ────────────────────────────────────────────────────

        void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            var gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");

            if (gmcm == null)
            {
                Debugger.DebugLog(Monitor, "GMCM not found – in-game config unavailable.", LogLevel.Debug);
                return;
            }

            gmcm.Register(
                mod: ModManifest,
                reset: () => { _config = new ModConfig(); MapTooltipDrawer.Config = _config; SetEnabled(_config.Enabled); },
                save: () => { Helper.WriteConfig(_config); MapTooltipDrawer.Config = _config; SetEnabled(_config.Enabled); });

            gmcm.AddBoolOption(
                mod: ModManifest,
                name: () => ModEntryUIStrings.EnabledStatus,
                tooltip: () => ModEntryUIStrings.EnabledStatusTooltip,
                getValue: () => _config.Enabled,
                setValue: v => { _config.Enabled = v; SetEnabled(v); });

            gmcm.AddTextOption(
                mod: ModManifest,
                name: () => ModEntryUIStrings.Display,
                tooltip: () => ModEntryUIStrings.DisplayTooltip,
                getValue: () => _config.Display.ToString(),
                setValue: v => _config.Display = Enum.Parse<DisplayMode>(v),
                allowedValues: new[] { ModEntryUIStrings.IconOptionBothDropdown, ModEntryUIStrings.IconOptionIconOnlyDropdown, ModEntryUIStrings.IconOptionTextOnlyDropdown },
                formatAllowedValue: v => v switch
                {
                    ModEntryUIStrings.IconOptionBothDropdown => ModEntryUIStrings.IconOptionBoth,
                    ModEntryUIStrings.IconOptionIconOnlyDropdown => ModEntryUIStrings.IconOptionIconOnly,
                    ModEntryUIStrings.IconOptionTextOnlyDropdown => ModEntryUIStrings.IconOptionTextOnly,
                    _ => v
                });

            gmcm.AddBoolOption(
                mod: ModManifest,
                name: () => ModEntryUIStrings.Amount,
                tooltip: () => ModEntryUIStrings.AmountTooltip,
                getValue: () => _config.ShowRemainingOnly,
                setValue: v => _config.ShowRemainingOnly = v);

            gmcm.AddNumberOption(
                mod: ModManifest,
                name: () => ModEntryUIStrings.IconScale,
                tooltip: () => ModEntryUIStrings.IconScaleTooltip,
                getValue: () => _config.IconScale,
                setValue: v => _config.IconScale = v,
                min: 0.5f, max: 3.0f, interval: 0.25f);

            // =========================================================
            // EDIT REGIONS BUTTON
            // =========================================================

            gmcm.AddComplexOption(
                mod: ModManifest,

                name: () => ModEntryUIStrings.Editor,

                tooltip: () => ModEntryUIStrings.EditorTooltip,

                beforeMenuOpened: () =>
                {
                    _configMenuOpen = true;
                },

                beforeMenuClosed: () =>
                {
                    _configMenuOpen = false;
                    _editRegionsButton = null;
                },

                draw: (spriteBatch, bounds) =>
                {
                    const int buttonWidth = 240;
                    const int buttonHeight = 64;

                    Rectangle rect = new(
                        (int)bounds.X,
                        (int)bounds.Y + 4,
                        buttonWidth,
                        buttonHeight
                    );

                    _editRegionsButton ??= new ClickableTextureComponent(
                        rect,
                        Game1.mouseCursors,
                        new Rectangle(128, 256, 60, 60),
                        1f
                    );

                    _editRegionsButton.bounds = rect;

                    bool hover = _editRegionsButton.containsPoint(
                        Game1.getMouseX(),
                        Game1.getMouseY());

                    _editRegionsButton.scale = hover ? 1.05f : 1f;

                    IClickableMenu.drawTextureBox(
                        spriteBatch,
                        rect.X,
                        rect.Y,
                        rect.Width,
                        rect.Height,
                        hover ? Color.Wheat : Color.White
                    );

                    Utility.drawTextWithShadow(
                        spriteBatch,
                        ModEntryUIStrings.EditRegions,
                        Game1.dialogueFont,
                        new Vector2(rect.X + 22, rect.Y + 16),
                        hover ? Color.DarkRed : Game1.textColor
                    );

                    // =====================================================
                    // CLICK HANDLING
                    // =====================================================

                    bool mouseDown = Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;

                    if (hover && mouseDown && !_editButtonWasDown)
                    {
                        Game1.playSound("bigSelect");
                        OpenEditor();
                    }

                    _editButtonWasDown = mouseDown;
                },

                height: () => 76
            );
            Debugger.DebugLog(Monitor, "GMCM registered.", LogLevel.Debug);
        }

        static char GetCharFromKey(Keys key)
        {
            bool shift =
                Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftShift) ||
                Game1.input.GetKeyboardState().IsKeyDown(Keys.RightShift);

            // Letters
            if (key >= Keys.A && key <= Keys.Z)
            {
                char c = (char)('a' + (key - Keys.A));
                return shift ? char.ToUpper(c) : c;
            }

            // Numbers
            if (key >= Keys.D0 && key <= Keys.D9)
            {
                return (char)('0' + (key - Keys.D0));
            }

            // Space
            if (key == Keys.Space)
                return ' ';

            // Period
            if (key == Keys.OemPeriod)
                return '.';

            // Comma
            if (key == Keys.OemComma)
                return ',';

            // Dash
            if (key == Keys.OemMinus)
                return '-';

            return '\0';
        }
    }

}