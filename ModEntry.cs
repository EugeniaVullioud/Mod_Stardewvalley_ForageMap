using LightRadiusMod;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

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
        // -------------------------------------------------------------------------
        // Fields
        // -------------------------------------------------------------------------

        private ModConfig _config = null!;
        private ForageTracker _tracker = null!;

        /// <summary>True while the tracking event handlers are subscribed.</summary>
        private bool _active = false;

        // -------------------------------------------------------------------------
        // Entry
        // -------------------------------------------------------------------------

        public override void Entry(IModHelper helper)
        {
            _config = helper.ReadConfig<ModConfig>();
            _tracker = new ForageTracker(Monitor);

            // ── Wire dependencies into the drawer ────────────────────────────────
            MapTooltipDrawer.Tracker = _tracker;
            MapTooltipDrawer.Config = _config;
            MapTooltipDrawer.Monitor = Monitor;

            // ── Always-on events ─────────────────────────────────────────────────
            // GameLaunched  → GMCM registration
            // SaveLoaded    → initial scan when a save is opened mid-session
            // RenderedActiveMenu → our draw hook (cheap early-exit when map is not open)
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;

            // Activate or stay dormant according to saved config
            SetEnabled(_config.Enabled);

            Monitor.Log(
                $"Forage Tracker loaded (initially {(_config.Enabled ? "enabled" : "disabled")}).",
                LogLevel.Info);
        }

        // -------------------------------------------------------------------------
        // State transition — subscribe / unsubscribe tracking events
        // -------------------------------------------------------------------------

        private void SetEnabled(bool enable)
        {
            if (enable == _active) return;

            if (enable)
            {
                Helper.Events.GameLoop.DayStarted += OnDayStarted;
                Helper.Events.World.ObjectListChanged += OnObjectListChanged;
                Monitor.Log("[ForageTracker] Tracking activated.", LogLevel.Debug);
            }
            else
            {
                Helper.Events.GameLoop.DayStarted -= OnDayStarted;
                Helper.Events.World.ObjectListChanged -= OnObjectListChanged;
                Monitor.Log("[ForageTracker] Tracking deactivated.", LogLevel.Debug);
            }

            _active = enable;
        }

        // -------------------------------------------------------------------------
        // Always-on handlers
        // -------------------------------------------------------------------------

        /// <summary>
        /// Passes the SpriteBatch straight to the drawer.
        /// The drawer exits immediately if the map tab is not open — negligible cost.
        /// </summary>
        private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
        {
            MapTooltipDrawer.OnRenderedActiveMenu(e.SpriteBatch);
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            if (_active)
            {
                _tracker.ScanAllLocations();
                MapTooltipDrawer.RebuildLocationKeyCache();
            }
        }

        // -------------------------------------------------------------------------
        // Tracking handlers (only subscribed while active)
        // -------------------------------------------------------------------------

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            _tracker.ScanAllLocations();
            MapTooltipDrawer.RebuildLocationKeyCache();
        }

        private void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
        {
            foreach (var (tile, obj) in e.Removed)
            {
                if (obj.IsSpawnedObject)
                {
                    _tracker.MarkPicked(e.Location.Name, tile);
                    Monitor.Log(
                        $"[ForageTracker] Picked: {obj.DisplayName} @ {tile} in {e.Location.Name}",
                        LogLevel.Trace);
                }
            }
        }

        // -------------------------------------------------------------------------
        // GMCM registration
        // -------------------------------------------------------------------------

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            var gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(
                "spacechase0.GenericModConfigMenu");

            if (gmcm == null)
            {
                Monitor.Log("GMCM not found – in-game config menu unavailable.", LogLevel.Debug);
                return;
            }

            gmcm.Register(
                mod: ModManifest,
                reset: () =>
                {
                    _config = new ModConfig();
                    MapTooltipDrawer.Config = _config;
                    SetEnabled(_config.Enabled);
                },
                save: () =>
                {
                    Helper.WriteConfig(_config);
                    MapTooltipDrawer.Config = _config;
                    SetEnabled(_config.Enabled);
                });

            gmcm.AddBoolOption(
                mod: ModManifest,
                name: () => "Enable Forage Tracker",
                tooltip: () => "Track and display daily forageable counts on the map tooltip.",
                getValue: () => _config.Enabled,
                setValue: v => { _config.Enabled = v; SetEnabled(v); });

            gmcm.AddTextOption(
                mod: ModManifest,
                name: () => "Display Mode",
                tooltip: () => "Choose what to show per forage entry: icon + text, icon only, or text only.",
                getValue: () => _config.Display.ToString(),
                setValue: v => _config.Display = Enum.Parse<DisplayMode>(v),
                allowedValues: new[] { "Both", "IconOnly", "TextOnly" },
                formatAllowedValue: v => v switch
                {
                    "Both" => "Icon + Text",
                    "IconOnly" => "Icon Only",
                    "TextOnly" => "Text Only",
                    _ => v
                });

            gmcm.AddBoolOption(
                mod: ModManifest,
                name: () => "Show Remaining Only",
                tooltip: () => "ON = show only un-picked forageables.  OFF = show remaining / total.",
                getValue: () => _config.ShowRemainingOnly,
                setValue: v => _config.ShowRemainingOnly = v);

            gmcm.AddNumberOption(
                mod: ModManifest,
                name: () => "Icon Scale",
                tooltip: () => "Extra multiplier on top of auto-scaling (1.0 = default, 2.0 = double).",
                getValue: () => _config.IconScale,
                setValue: v => _config.IconScale = v,
                min: 0.5f,
                max: 3.0f,
                interval: 0.25f);

            Monitor.Log("GMCM registered.", LogLevel.Debug);
        }
    }
}