using ForageTrackerModSV;
using ForageTrackerModSV.Compatibility;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Text.RegularExpressions;

namespace ForageTrackerMod
{
    /// <summary>
    /// Resolves vanilla map hover-point names to their actual GameLocation names.
    ///
    /// THE CORE PROBLEM
    /// ─────────────────
    /// mapPage.points component names (e.g. "AlexHouse", "Beach/ElliottCabin")
    /// are arbitrary strings with NO guaranteed relationship to GameLocation.Name
    /// ("JoshHouse", "ElliottHouse"). String matching alone fails.
    ///
    /// THE AUTHORITATIVE SOURCE
    /// ─────────────────────────
    /// mapPage.mapAreas[].Data.WorldPositions[].LocationName IS the real
    /// GameLocation.Name — SDV's own mapping. We index this in EnsureBuilt.
    ///
    /// We then also walk mapPage.points and match each point to the area whose
    /// WorldPositions entries it falls inside (by bounding box), giving us a
    /// direct point.name → [GameLocation names] mapping that handles all cases:
    ///   "AlexHouse"        → area overlapping that point → WorldPositions → "JoshHouse"
    ///   "Beach/ElliottCabin" → sub-name extraction + area lookup → "ElliottHouse"
    ///   "Beach"            → direct WorldPositions → "Beach"
    ///
    /// CANDIDATE GATHERING (GatherCandidates)
    /// ────────────────────────────────────────
    /// For a given point.name we collect candidates from:
    ///   1. Direct point→locations index (built from point-to-area spatial match)
    ///   2. Slash sub-name extraction ("Beach/ElliottCabin" → "ElliottCabin")
    ///   3. LocationHierarchy children of each WorldPositions location
    ///      (because the point may represent a building whose interior is a child)
    ///   4. SDV resolver (Game1.getLocationFromName) on name and sub-name
    ///   5. Substring search against LocationHierarchy.AllLocations (last resort)
    ///
    /// SCORING (Score)
    /// ────────────────
    /// Each candidate is scored:
    ///   +100  in tracker (has/could have forage — most important)
    ///   +80   exact match on slash sub-name (strongest string evidence)
    ///   +60   is a direct child of a WorldPositions location for this point
    ///   +50   IndoorMap or FarmBuilding type (specific, not a broad area)
    ///   +30   pointName contains candidate name (string evidence)
    ///   +20   candidate name contains pointName
    ///   +10   only one candidate (unambiguous)
    ///   −50   Outdoor type (broad area, penalised when indoor candidates exist)
    ///   −100  is a parent/ancestor of another candidate (too broad)
    ///
    /// Highest scorer wins. If no candidate is tracked, the best-typed candidate
    /// is still returned so GetRegionAtPoint can apply the building-silence rule.
    /// </summary>
    public static class MapPointResolver
    {
        // point.name → candidate GameLocation names (ordered, best first from WorldPositions)
        private static readonly Dictionary<string, List<string>> _pointToLocations = new(StringComparer.OrdinalIgnoreCase);

        // GameLocation.Name → point.name that covers it (reverse lookup)
        private static readonly Dictionary<string, string> _locationToPoint = new(StringComparer.OrdinalIgnoreCase);

        // WorldPositions location names per area.Id (area.Id → locations)
        private static readonly Dictionary<string, List<string>> _areaToLocations = new(StringComparer.OrdinalIgnoreCase);

        private static MapPage? _lastPage;
        private static IMonitor? _monitor;

        public static void Init(IMonitor monitor) => _monitor = monitor;

        // ── Public API ────────────────────────────────────────────────────────
        private static bool IsCandidateForMapPoint(string pointName, string candidate)
        {
            if (string.IsNullOrWhiteSpace(pointName) || string.IsNullOrWhiteSpace(candidate)) return false;

            // The candidate must be represented in the current map's
            // spatially indexed locations for this point.
            if (_pointToLocations.TryGetValue(pointName, out var locations) && locations.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return true;

            // Explicit slash identifiers are valid map-specific evidence.
            int slash = pointName.IndexOf('/');

            if (slash < 0)
                return false;

            string parent = pointName[..slash];
            string child = pointName[(slash + 1)..];

            // Default points represent their parent area.
            if (string.Equals(
                child,
                "Default",
                StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(
                    parent,
                    candidate,
                    StringComparison.OrdinalIgnoreCase);
            }

            // Direct identifier match.
            if (string.Equals(
                child,
                candidate,
                StringComparison.OrdinalIgnoreCase))
                return true;

            // Normalized identifier match, but only within the same area scope.
            string normalizedChild = NormalizeLocation(child);
            string normalizedCandidate = NormalizeLocation(candidate);

            if (string.Equals(
                normalizedChild,
                normalizedCandidate,
                StringComparison.OrdinalIgnoreCase) &&
                InSameAreaScope(pointName, candidate))
            {
                return true;
            }

            return false;
        }
        private static bool _locationIndexBuilt;

        /// <summary>
        /// Build the indexes from mapPage data. Cached per MapPage instance.
        /// </summary>
        public static void EnsureBuilt(MapPage page, string mapKey)
        {
            if (ReferenceEquals(page, _lastPage) && string.Equals(_lastMapKey, mapKey, StringComparison.OrdinalIgnoreCase)) return;

            _lastPage = page;
            _lastMapKey = mapKey;

            if (!_locationIndexBuilt) BuildLocationIndex();
            else RefreshLocationIndex();

            _pointToLocations.Clear();
            _locationToPoint.Clear();
            _areaToLocations.Clear();

            try
            {
                // ── Phase 1: index area.Id → WorldPositions locations ─────────
                foreach (var area in page.mapAreas)
                {
                    var locs = new List<string>();
                    foreach (var wp in area.Data.WorldPositions)
                    {
                        if (string.IsNullOrEmpty(wp.LocationName)) continue;
                        locs.Add(wp.LocationName);
                        if (!_locationToPoint.ContainsKey(wp.LocationName))
                            _locationToPoint[wp.LocationName] = area.Id;
                    }
                    if (locs.Count > 0)
                        _areaToLocations[area.Id] = locs;

                    _monitor?.Log(
                        $"[Resolver] area '{area.Id}' → [{string.Join(", ", locs)}]",
                        LogLevel.Trace);
                }

                // ── Phase 2: index point.name → locations ─────────────────────
                // For each map point, find which area it spatially belongs to.
                // This resolves "AlexHouse" → the area whose WorldPositions
                // includes "JoshHouse", without any string matching.
                var points = MapPageCompat.GetPoints(page);
                foreach (var point in points)
                {
                    if (string.IsNullOrEmpty(point.name)) continue;
                    if (_pointToLocations.ContainsKey(point.name)) continue;

                    var candidates = new List<string>();

                    // Find the area whose rendered pixel rect contains this point.
                    // We union all texture rects for the area (same approach as
                    // MapRenderUtility.ComputeActualMapRect) and check containment.
                    foreach (var area in page.mapAreas)
                    {
                        try
                        {
                            // Union all texture rects for this area.
                            Microsoft.Xna.Framework.Rectangle areaRect = Microsoft.Xna.Framework.Rectangle.Empty;
                            bool first = true;
                            foreach (var tex in area.GetTextures())
                            {
                                var r = tex.GetOffsetMapPixelArea(page.mapBounds.X, page.mapBounds.Y);
                                areaRect = first ? r : Microsoft.Xna.Framework.Rectangle.Union(areaRect, r);
                                first = false;
                            }
                            // Also include the base texture if present.
                            var baseTexData = area.Data?.Textures?.FirstOrDefault();
                            var baseTex = baseTexData?.Texture; // or whatever property holds the loaded texture
                            if (baseTex != null)
                            {
                                var source = baseTexData.SourceRect; // if it exists

                                var r = new Microsoft.Xna.Framework.Rectangle(
                                    page.mapBounds.X,
                                    page.mapBounds.Y,
                                    source.Width,
                                    source.Height
                                );
                                areaRect = first ? r : Microsoft.Xna.Framework.Rectangle.Union(areaRect, r);
                            }

                            if (!first && areaRect.Contains(point.bounds.Center))
                            {
                                if (_areaToLocations.TryGetValue(area.Id, out var areaLocs))
                                    foreach (var loc in areaLocs)
                                        if (!candidates.Contains(loc, StringComparer.OrdinalIgnoreCase))
                                            candidates.Add(loc);
                            }
                        }
                        catch { }
                    }

                    // Also try slash sub-name as a direct location name.
                    int slash = point.name.IndexOf('/');
                    if (slash >= 0)
                    {
                        string parent = point.name[..slash];
                        string sub = point.name[(slash + 1)..];

                        if (string.Equals(sub, "Default", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!candidates.Contains(parent, StringComparer.OrdinalIgnoreCase)) candidates.Insert(0, parent);
                        }
                        else if (!string.IsNullOrEmpty(sub) && !candidates.Contains(sub, StringComparer.OrdinalIgnoreCase))
                        {
                            candidates.Insert(0, sub);
                        }
                    }

                    if (candidates.Count > 0)
                    {
                        _pointToLocations[point.name] = candidates;
                        _monitor?.Log(
                            $"[Resolver] point '{point.name}' → [{string.Join(", ", candidates)}]",
                            LogLevel.Trace);
                    }
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[Resolver] Build error: {ex.Message}", LogLevel.Warn);
            }
        }
        private static readonly HashSet<string> _resolutionLocked = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Resolves a map point name to every internal GameLocation it maps to.
        ///
        /// ── PRIMARY PATH: manual mapping ──────────────────────────────────────
        /// If the user has assigned this hoverable via the editor's "Edit Map
        /// Hover Data Relationships" panel, that mapping is authoritative and
        /// returned as-is (can be 1 or many locations). The algorithmic scoring
        /// below is never consulted in this case.
        ///
        /// ── FALLBACK PATH: legacy scoring algorithm ───────────────────────────
        /// Only used for hoverables that have NOT been manually mapped yet, so
        /// existing/unmigrated maps keep working. Always returns at most one
        /// candidate (the old single-best-guess behavior), wrapped in a list.
        ///
        /// Returns an empty list if nothing could be resolved either way.
        /// </summary>
        /// <param name="mapKey">
        /// Editor map key for the currently displayed map (same key used by
        /// MapRegionConfig.RegionsByMap), used to scope the manual-mapping
        /// lookup per-map.
        /// </param>
        public static List<string> ResolveAll(string mapKey, string pointName, ForageTracker? tracker)
        {
            if (string.IsNullOrEmpty(pointName)) return new List<string>();

            // ── Existing saved mappings + custom mappings ─────────────────────
            var manual = HoverMappingStore.GetLocations(mapKey, pointName);
            var custom = CustomHoverMappingStore.GetLocations(mapKey, pointName);

            // If either mapping exists, merge both with automatic discovery.
            if (manual != null || custom != null)
            {
                var combined = new List<string>();

                void AddValid(string location)
                {
                    if (string.IsNullOrWhiteSpace(location))
                        return;

                    if (!IsRealLocation(location))
                    {
                        _monitor?.Log(
                            $"[Resolver][MERGE INVALID] '{pointName}' => '{location}'",
                            LogLevel.Warn);
                        return;
                    }

                    if (!combined.Contains(location, StringComparer.OrdinalIgnoreCase))
                        combined.Add(location);
                }

                // 1. Preserve existing saved mappings.
                if (manual != null)
                {
                    foreach (var location in manual)
                        AddValid(location);
                }

                // 2. Add custom mappings.
                if (custom != null)
                {
                    foreach (var location in custom)
                        AddValid(location);
                }

                // 3. Add automatically discovered best candidate.
                _resolutionLocked.Clear();

                var (strictC, fallbackC) = GatherCandidates2(pointName);
                var discovered = strictC.Count > 0 ? strictC : fallbackC;

                if (discovered.Count > 0)
                {
                    var candidateSet = new HashSet<string>(
                        discovered,
                        StringComparer.OrdinalIgnoreCase);

                    string? bestC = null;
                    int bestScoreC = int.MinValue;

                    foreach (var candidate in discovered)
                    {
                        int score = Score(
                            candidate,
                            pointName,
                            tracker,
                            discovered,
                            candidateSet);

                        if (score > bestScoreC)
                        {
                            bestScoreC = score;
                            bestC = candidate;
                        }
                    }

                    if (bestC != null)
                        AddValid(bestC);
                }

                _monitor?.Log(
                    $"[Resolver][MERGED RESULT] Map='{mapKey}', " +
                    $"Point='{pointName}' => [{string.Join(", ", combined)}]",
                    LogLevel.Info);

                return combined;
            }

            // ── Default hoverables represent their parent area ────────────────
            int slash = pointName.IndexOf('/');
            if (slash > 0)
            {
                string parent = pointName[..slash];
                string child = pointName[(slash + 1)..];

                if (string.Equals(child, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    _monitor?.Log($"[Resolver] '{pointName}' → parent area '{parent}'", LogLevel.Debug);

                    return IsRealLocation(parent) ? new List<string> { parent } : new List<string>();
                }
            }
            _monitor?.Log(
    $"[Resolver][LOCATION TEST] " +
    $"LeahCottage={IsRealLocation("LeahCottage")}, " +
    $"LeahHouse={IsRealLocation("LeahHouse")}",
    LogLevel.Debug);

            // ── Legacy algorithmic fallback (unmapped hoverables only) ────────
            _resolutionLocked.Clear();
            (List<string> strict, List<string> fallback) = GatherCandidates2(pointName);
            var candidates = strict.Count > 0 ? strict : fallback;

            if (candidates.Count == 0)
            {
                _monitor?.Log($"[Resolver] '{pointName}' → no candidates", LogLevel.Debug);
                return new List<string>();
            }

            string? best = null;
            int bestScore = int.MinValue;

            // Track which candidates are ancestors of others — used to penalise
            // overly broad results when more specific ones exist.
            var allCandidateSet = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                int score = Score(candidate, pointName, tracker, candidates, allCandidateSet);
                _monitor?.Log(
                    $"[Resolver] '{pointName}' candidate '{candidate}' " +
                    $"score={score} type={LocationHierarchy.GetLocationType(candidate)} " +
                    $"tracked={tracker?.IsTracked(candidate)}",
                    LogLevel.Debug);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            _monitor?.Log($"[Resolver] '{pointName}' → '{best}' (score={bestScore})", LogLevel.Debug);
            if (best == null) return new List<string>();

            _resolutionLocked.Add(best);
            return new List<string> { best };
        }
        public static bool IsRealLocation(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            /*
            try
            {
                return Game1.getLocationFromName(name) != null;
            }
            catch
            {
                return false;
            }
            */
            bool found = false;


            try
            {
                /*
                foreach (var locName in LocationHierarchy.AllLocations)
                {
                    if (string.Equals(locName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        return false; // Stop searching.
                    }

                    return true; // Continue searching.
                }
                */
                StardewValley.Utility.ForEachLocation(location =>
                {
                    if (string.Equals(location.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        return false; // Stop searching.
                    }

                    return true; // Continue searching.
                }, includeInteriors: true);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[Resolver] Validation failed for '{name}': {ex.Message}", LogLevel.Warn);
                return false;
            }
            return found;
        }
        static HashSet<string> GetAllowedClusterLocations(MapPage page, string pointName)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // find the area this point belongs to (same logic you already use)
            var points = MapPageCompat.GetPoints(page);
            var targetPoint = points.FirstOrDefault(p => p.name == pointName);

            if (targetPoint == null)
                return allowed;

            foreach (var area in page.mapAreas)
            {
                try
                {
                    Microsoft.Xna.Framework.Rectangle areaRect = Microsoft.Xna.Framework.Rectangle.Empty;
                    bool first = true;

                    foreach (var tex in area.GetTextures())
                    {
                        var r = tex.GetOffsetMapPixelArea(page.mapBounds.X, page.mapBounds.Y);
                        areaRect = first ? r : Microsoft.Xna.Framework.Rectangle.Union(areaRect, r);
                        first = false;
                    }

                    var baseTexData = area.Data?.Textures?.FirstOrDefault();
                    var baseTex = baseTexData?.Texture;

                    if (baseTex != null)
                    {
                        var source = baseTexData.SourceRect;

                        var r = new Microsoft.Xna.Framework.Rectangle(
                            page.mapBounds.X,
                            page.mapBounds.Y,
                            source.Width,
                            source.Height
                        );

                        areaRect = first ? r : Microsoft.Xna.Framework.Rectangle.Union(areaRect, r);
                    }

                    if (!first && areaRect.Contains(targetPoint.bounds.Center))
                    {
                        // THIS is the cluster root
                        if (_areaToLocations.TryGetValue(area.Id, out var locs))
                        {
                            foreach (var loc in locs)
                            {
                                allowed.Add(loc);

                                // include hierarchy children (Robin house → SebastianRoom etc.)
                                foreach (var child in LocationHierarchy.GetChildren(loc))
                                    allowed.Add(child);
                            }
                        }
                    }
                }
                catch { }
            }

            return allowed;
        }

        private static readonly LocationNameIndex _locationIndex = new();

        public static void RegisterLocation(string locationName)
        {
            _locationIndex.Add(locationName);
        }
        public static void RefreshLocationIndex()
        {
            foreach (var location in Game1.locations)
            {
                if (location == null)
                    continue;

                _locationIndex.Add(location.Name);
            }

            foreach (var location in LocationHierarchy.AllLocations)
            {
                _locationIndex.Add(location);
            }

            _monitor?.Log(
                $"[Resolver][INDEX REFRESH] Count={_locationIndex.Count}, " +
                $"WizardHouse={_locationIndex.ContainsOriginal("WizardHouse")}",
                LogLevel.Debug);
        }
        static void BuildLocationIndex()
        {
            _locationIndex.Clear();

            foreach (var location in LocationHierarchy.AllLocations)
            {
                RegisterLocation(location);
            }

            RefreshLocationIndex();

            _locationIndexBuilt = true;

            _monitor?.Log(
                $"[Resolver] Location index built: {_locationIndex.Count} locations",
                LogLevel.Debug);
        }
        // ── Candidate gathering ───────────────────────────────────────────────
        static (List<string> strict, List<string> fallback) GatherCandidates2(string pointName)
        {
            var strictSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fallbackSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var strict = new List<string>();
            var fallback = new List<string>();

            var allowedCluster = GetAllowedClusterLocations(page: _lastPage!, pointName);

            void AddStrict(string? name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                if (!IsRealLocation(name)) return;
                if (!IsCandidateForMapPoint(pointName, name)) return;

                bool inScope = InSameAreaScope(pointName, name);

                if (!inScope) return;
                if (!HasStrongNameMatch(pointName, name) && !IsDirectLocationRelationship(pointName, name)) return;

                if (strictSeen.Add(name)) strict.Add(name);
            }

            void AddFallback(string? name)
            {
                if (string.IsNullOrWhiteSpace(name) || !IsRealLocation(name)) return;

                if (!IsCandidateForMapPoint(pointName, name)) return;

                bool inScope = InSameAreaScope(pointName, name);

                if (!inScope) return;

                if (!fallbackSeen.Add(name)) return;

                if (!HasStrongNameMatch(pointName, name) && !IsDirectLocationRelationship(pointName, name)) return;

                fallback.Add(name);
            }

            // ─────────────────────────────────────────────
            // PHASE 1: STRICT (authoritative only)
            // ─────────────────────────────────────────────

            // 1. Spatial mapping (most reliable)
            if (_pointToLocations.TryGetValue(pointName, out var direct))
            {
                foreach (var n in direct)
                {
                    AddStrict(n);
                }
            }

            // 2. Slash sub-name exact + SDV lookup ONLY
            int slash = pointName.IndexOf('/');
            if (slash >= 0)
            {
                string parent = pointName[..slash];
                string sub = pointName[(slash + 1)..];

                if (string.Equals(sub, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    // A Default hoverable represents the parent area itself.
                    AddStrict(parent);

                    try
                    {
                        AddStrict(Game1.getLocationFromName(parent)?.Name);
                    }
                    catch { }
                }

                else if (!string.IsNullOrEmpty(sub))
                {

                    AddStrict(sub);

                    try
                    {
                        var location = Game1.getLocationFromName(sub);

                        AddStrict(location?.Name);
                    }
                    catch (Exception ex)
                    {
                        _monitor?.Log(
                            $"[Resolver][GAMELOOKUP ERROR] {ex.Message}",
                            LogLevel.Warn);
                    }

                    int underscore = sub.IndexOf('_');
                    if (underscore > 0)
                    {
                        string stripped = sub[..underscore];
                        AddStrict(stripped);

                        try
                        {
                            AddStrict(Game1.getLocationFromName(stripped)?.Name);
                        }
                        catch { }
                    }
                }

                if (_areaToLocations.TryGetValue(parent, out var areaLocs))
                {
                    foreach (var n in areaLocs) AddStrict(n);
                }
            }
            // 2B. Normalized location matching.
            // Discover actual game locations whose normalized names match the hover identifier, without requiring identical suffixes.

            //if (slash >= 0 && _pointToLocations.TryGetValue(pointName, out var mapCandidates))
            if (slash >= 0)
            {
                foreach (var location in FindLoadedNormalizedMatches(pointName, _lastPage!))
                {
                    if (!IsRealLocation(location)) continue;

                    if (strictSeen.Add(location))
                    {
                        strict.Add(location);

                        _monitor?.Log(
                            $"[Resolver][NORMALIZED DISCOVERY] " +
                            $"Point='{pointName}', " +
                            $"Candidate='{location}'",
                            LogLevel.Info);
                    }
                }

            }

            // 3. Direct SDV lookup on full name
            try { AddStrict(Game1.getLocationFromName(pointName)?.Name); } catch { }

            // ─────────────────────────────────────────────
            // PHASE 2: FALLBACK (ONLY if strict fails)
            // ─────────────────────────────────────────────

            void AddFallbackSafe(string name) => AddFallback(name);

            // hierarchy expansion ONLY in fallback
            var snapshot = strict.ToList();
            foreach (var loc in snapshot)
            {
                foreach (var child in LocationHierarchy.GetChildren(loc))
                {
                    if (allowedCluster.Contains(child))
                        AddFallbackSafe(child);
                }
            }

            // substring search ONLY in fallback
            string lower = pointName.ToLowerInvariant();
            string subLower = slash >= 0 ? pointName[(slash + 1)..].ToLowerInvariant() : lower;

            int ul = subLower.IndexOf('_');
            string subStrippedLower = ul > 0 ? subLower[..ul] : subLower;

            var mapScopedCandidates = _pointToLocations.TryGetValue(pointName, out var pointLocations) ? pointLocations : new List<string>();

            foreach (var locName in mapScopedCandidates)
            {
                string locLower = locName.ToLowerInvariant();

                if (!string.IsNullOrEmpty(subStrippedLower) &&
                    (locLower.Contains(subStrippedLower) || subStrippedLower.Contains(locLower)))
                    AddFallbackSafe(locName);

                else if (locLower.Contains(subLower) || subLower.Contains(locLower))
                    AddFallbackSafe(locName);

                else if (lower.Contains(locLower) || locLower.Contains(lower))
                    AddFallbackSafe(locName);
            }

            return (strict, fallback);
        }
        private static bool IsDirectLocationRelationship(string pointName, string candidate)
        {
            int slash = pointName.IndexOf('/');

            if (slash < 0)
                return false;

            string identifier = pointName[(slash + 1)..];

            int underscore = identifier.IndexOf('_');

            if (underscore >= 0)
                identifier = identifier[..underscore];

            return string.Equals(
                identifier,
                candidate,
                StringComparison.OrdinalIgnoreCase);
        }
        private static string? _lastMapKey;

        public static List<string> GetNormalizedMatches(string mapKey, string pointName)
        {
            var results = new List<string>();

            if (string.IsNullOrWhiteSpace(mapKey) || string.IsNullOrWhiteSpace(pointName)) return results;

            int slash = pointName.IndexOf('/');
            if (slash < 0) return results;

            string sub = pointName[(slash + 1)..];
            string normalized = NormalizeLocation(sub);

            if (string.IsNullOrWhiteSpace(normalized)) return results;

            // Get the hoverable's parent map area.
            string parentArea = slash >= 0
                ? pointName[..slash]
                : string.Empty;

            // Get the internal locations associated with that area.
            var areaLocations = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(parentArea) &&
                _areaToLocations.TryGetValue(parentArea, out var locations))
            {
                foreach (var location in locations)
                    areaLocations.Add(location);
            }
            _monitor?.Log(
    $"[Resolver][WIZARD CHECK] " +
    $"WizardHouse Real={IsRealLocation("WizardHouse")}, " +
    $"WizardHouse Parent={LocationHierarchy.GetParent("WizardHouse") ?? "NULL"}",
    LogLevel.Info);
            // Expand the area using its internal location hierarchy.
            var mapLocations = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            _monitor?.Log(
    "[Resolver][GAME LOCATIONS] Searching loaded locations",
    LogLevel.Info);

            foreach (var location in Game1.locations)
            {
                _monitor?.Log(
                    $"[Resolver][GAME LOCATION] " +
                    $"Name='{location.Name}', " +
                    $"Normalized='{NormalizeLocation(location.Name)}'",
                    LogLevel.Info);
            }
            foreach (var location in areaLocations)
            {
                mapLocations.Add(location);
                _monitor?.Log(
       $"[Resolver][AREA ROOT] {location}",
       LogLevel.Info);

                foreach (var child in LocationHierarchy.GetChildren(location))
                {
                    mapLocations.Add(child);
                    _monitor?.Log(
            $"[Resolver][AREA CHILD] {location} -> {child}",
            LogLevel.Info);
                }
            }

            // Also include the parent area itself.
            if (!string.IsNullOrEmpty(parentArea))
                mapLocations.Add(parentArea);

            _monitor?.Log(
                $"[Resolver][NORMALIZED SEARCH] " +
                $"Point='{pointName}', " +
                $"ParentArea='{parentArea}', " +
                $"Normalized='{normalized}', " +
                $"Candidates={mapLocations.Count}",
                LogLevel.Debug);

            foreach (var location in mapLocations)
            {
                if (!IsRealLocation(location))
                    continue;

                string normalizedLocation = NormalizeLocation(location);

                if (!string.Equals(
                    normalized,
                    normalizedLocation,
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                _monitor?.Log(
                    $"[Resolver][NORMALIZED MATCH] " +
                    $"Point='{pointName}', " +
                    $"Candidate='{location}'",
                    LogLevel.Info);

                results.Add(location);
            }

            return results;

        }

        private static List<string> FindLoadedNormalizedMatches(string pointName, MapPage page)
        {
            var results = new List<string>();

            int slash = pointName.IndexOf('/');
            if (slash < 0) return results;

            string sub = pointName[(slash + 1)..];

            // Remove map-point variants such as ExtendedHours.
            int underscore = sub.IndexOf('_');
            if (underscore > 0) sub = sub[..underscore];

            string normalizedPoint = NormalizeLocation(sub);

            if (string.IsNullOrWhiteSpace(normalizedPoint)) return results;

            RefreshLocationIndex();

            foreach (var entry in _locationIndex.FindNormalized(normalizedPoint))
            {
                if (!IsRealLocation(entry.OriginalName)) continue;

                results.Add(entry.OriginalName);

                _monitor?.Log(
                    $"[Resolver][INDEX MATCH] " +
                    $"Point='{pointName}', " +
                    $"Original='{entry.OriginalName}', " +
                    $"Normalized='{entry.NormalizedName}'",
                    LogLevel.Debug);
            }

            return results
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        /// <summary>
        /// Returns true if the candidate is a reasonable resolution target for
        /// this point. The check is deliberately permissive — the scorer handles
        /// specificity. We only reject candidates that are completely unrelated
        /// to the point's area cluster.
        ///
        /// Previous implementation used string containment of the area prefix,
        /// which incorrectly rejected "ElliottHouse" for "Beach/ElliottCabin"
        /// because "ElliottHouse" doesn't contain the string "Beach".
        /// </summary>
        private static bool InSameAreaScope(string pointName, string candidate)
        {
            if (_pointToLocations.TryGetValue(pointName, out var clusterLocs))
            {
                // Preserve existing exact-match behavior.
                if (clusterLocs.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return true;

                // Preserve existing hierarchy behavior.
                string? parent = LocationHierarchy.GetParent(candidate);

                if (parent != null && clusterLocs.Contains(parent, StringComparer.OrdinalIgnoreCase)) return true;

                // NEW: Recognize structural name equivalents. Example: ElliottCabin == ElliottHouse
                string normalizedCandidate = NormalizeLocation(candidate);

                foreach (var clusterLocation in clusterLocs)
                {
                    if (string.Equals(NormalizeLocation(clusterLocation), normalizedCandidate, StringComparison.OrdinalIgnoreCase)) return true;
                }

                // Keep the original strict area boundary.
                if (clusterLocs.Count > 0) return false;
            }
            return true;
        }
        static readonly HashSet<string> IgnoredLocationWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "Cabin",
            "House",
            "Cottage",
            "Home",
            "Shop",
            "Tent",
            "Tower"
        };

        internal static string NormalizeLocation(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            name = name.Trim();

            // Remove map-point variants such as FishShop_ExtendedHours.
            name = Regex.Replace(name, @"_ExtendedHours$", "", RegexOptions.IgnoreCase);

            // Treat underscores as word separators.
            name = name.Replace("_", " ");

            // Split CamelCase: ElliottCabin -> Elliott Cabin.
            name = Regex.Replace(name, @"([a-z0-9])([A-Z])", "$1 $2");

            var parts = name.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

            int end = parts.Length;

            while (end > 0 && IgnoredLocationWords.Contains(parts[end - 1]))
                end--;

            return string.Join(" ", parts.Take(end)).Trim();
        }
        private static bool HasStrongNameMatch(string pointName, string candidate)
        {
            if (string.IsNullOrWhiteSpace(pointName) || string.IsNullOrWhiteSpace(candidate)) return false;

            int slash = pointName.IndexOf('/');

            string identifier = slash >= 0 ? pointName[(slash + 1)..] : pointName;

            // Remove map-point variant suffixes.
            // Example: MovieTheater_CommunityCenterRoute becomes MovieTheater.
            int underscore = identifier.IndexOf('_');

            if (underscore >= 0) identifier = identifier[..underscore];

            string normalizedPoint = NormalizeLocation(identifier);
            string normalizedCandidate = NormalizeLocation(candidate);

            return !string.IsNullOrWhiteSpace(normalizedPoint) && string.Equals(normalizedPoint, normalizedCandidate, StringComparison.OrdinalIgnoreCase);
        }
        private static int Score(string candidate, string pointName, ForageTracker? tracker, List<string> allCandidates, HashSet<string> allCandidateSet)
        {
            if (!IsRealLocation(candidate))
                return int.MinValue;

            if (!HasStrongNameMatch(pointName, candidate) &&
                !IsDirectLocationRelationship(pointName, candidate))
                return int.MinValue;

            int score = 0;

            if (HasStrongNameMatch(pointName, candidate))
                score += 100;

            if (IsDirectLocationRelationship(pointName, candidate))
                score += 50;

            // Tracker relevance is supplementary.
            if (tracker?.IsTracked(candidate) == true)
                score += 5;

            var type = LocationHierarchy.GetLocationType(candidate);

            if (type == LocationType.IndoorMap ||
                type == LocationType.FarmBuilding)
                score += 5;

            return score;
        }
    }
}
