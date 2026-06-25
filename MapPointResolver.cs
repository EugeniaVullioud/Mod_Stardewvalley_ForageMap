using ForageTrackerModSV.Compatibility;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Pets;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
using static StardewValley.LocationRequest;

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
        private static readonly Dictionary<string, List<string>> _pointToLocations =
            new(StringComparer.OrdinalIgnoreCase);

        // GameLocation.Name → point.name that covers it (reverse lookup)
        private static readonly Dictionary<string, string> _locationToPoint =
            new(StringComparer.OrdinalIgnoreCase);

        // WorldPositions location names per area.Id (area.Id → locations)
        private static readonly Dictionary<string, List<string>> _areaToLocations =
            new(StringComparer.OrdinalIgnoreCase);

        private static MapPage? _lastPage;
        private static IMonitor? _monitor;

        public static void Init(IMonitor monitor) => _monitor = monitor;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Build the indexes from mapPage data. Cached per MapPage instance.
        /// </summary>
        public static void EnsureBuilt(MapPage page)
        {
            if (ReferenceEquals(page, _lastPage)) return;
            _lastPage = page;
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
                        string sub = point.name[(slash + 1)..];
                        if (!string.IsNullOrEmpty(sub) && !candidates.Contains(sub))
                            candidates.Insert(0, sub); // sub-name is most specific, put first
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
        private static readonly HashSet<string> _resolutionLocked =
    new(StringComparer.OrdinalIgnoreCase);
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

            // ── Manual mapping takes full priority ────────────────────────────
            var manual = HoverMappingStore.GetLocations(mapKey, pointName);
            if (manual != null)
            {
                _monitor?.Log(
                    $"[Resolver] '{pointName}' → manual mapping [{string.Join(", ", manual)}]",
                    LogLevel.Debug);
                return manual;
            }

            // ── Legacy algorithmic fallback (unmapped hoverables only) ────────
            _resolutionLocked.Clear();
            (List<string> strict, List<string> fallback) = GatherCandidates2(pointName);
            var candidates = strict.Count > 0 ? strict : fallback;

            if (candidates.Count == 0)
            {
                _monitor?.Log($"[Resolver] '{pointName}' → no candidates", LogLevel.Debug);
                return new List<string>();
            }

            string? best      = null;
            int     bestScore = int.MinValue;

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
                    best      = candidate;
                }
            }

            _monitor?.Log($"[Resolver] '{pointName}' → '{best}' (score={bestScore})", LogLevel.Debug);
            if (best == null) return new List<string>();

            _resolutionLocked.Add(best);
            return new List<string> { best };
        }
        private static int ClusterScore(string candidate, string areaId)
        {
            if (_areaToLocations.TryGetValue(areaId, out var locs))
            {
                if (locs.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    return 100;

                foreach (var loc in locs)
                {
                    if (LocationHierarchy.GetChildren(loc)
                        .Any(c => c.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                        return 60;

                    if (candidate.Contains(loc, StringComparison.OrdinalIgnoreCase) ||
                        loc.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                        return 20;
                }
            }

            return -50;
        }
        public static string? GetPointForLocation(string locationName) =>
            _locationToPoint.TryGetValue(locationName, out var p) ? p : null;
        private static HashSet<string> GetAllowedClusterLocations(MapPage page, string pointName)
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
        // ── Candidate gathering ───────────────────────────────────────────────
        private static (List<string> strict, List<string> fallback) GatherCandidates2(string pointName)
        {
            var strictSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fallbackSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var strict = new List<string>();
            var fallback = new List<string>();

            var allowedCluster = GetAllowedClusterLocations(page: _lastPage!, pointName);

            void AddStrict(string? name)
            {
                if (string.IsNullOrEmpty(name)) return;

                if (!InSameAreaScope(pointName, name)) return;

                if (strictSeen.Add(name))
                    strict.Add(name);
            }

            void AddFallback(string? name)
            {
                if (string.IsNullOrEmpty(name)) return;

                if (!InSameAreaScope(pointName, name)) return;

                if (fallbackSeen.Add(name))
                    fallback.Add(name);
            }

            // ─────────────────────────────────────────────
            // PHASE 1: STRICT (authoritative only)
            // ─────────────────────────────────────────────

            // 1. Spatial mapping (most reliable)
            if (_pointToLocations.TryGetValue(pointName, out var direct))
            {
                foreach (var n in direct)
                    AddStrict(n);
            }

            // 2. Slash sub-name exact + SDV lookup ONLY
            int slash = pointName.IndexOf('/');
            if (slash >= 0)
            {
                string sub = pointName[(slash + 1)..];

                if (!string.IsNullOrEmpty(sub))
                {
                    AddStrict(sub);

                    try { AddStrict(Game1.getLocationFromName(sub)?.Name); } catch { }

                    int underscore = sub.IndexOf('_');
                    if (underscore > 0)
                    {
                        string stripped = sub[..underscore];
                        AddStrict(stripped);
                        try { AddStrict(Game1.getLocationFromName(stripped)?.Name); } catch { }
                    }
                }

                if (_areaToLocations.TryGetValue(pointName[..slash], out var areaLocs))
                {
                    foreach (var n in areaLocs)
                        AddStrict(n);
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

            foreach (var locName in LocationHierarchy.AllLocations)
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
        private static List<string> GatherCandidates(string pointName)
        {
            var seen       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>();

            void Add(string? name)
            {
                if (!string.IsNullOrEmpty(name) && seen.Add(name!))
                    candidates.Add(name!);
            }

            // 1. Point-to-locations index (spatial match built in EnsureBuilt).
            //    This is the most authoritative source — no string matching needed.
            if (_pointToLocations.TryGetValue(pointName, out var direct))
                foreach (var n in direct) Add(n);

            // 2. Slash sub-name: "Beach/FishShop_ExtendedHours" → "FishShop"
            //
            // The sub-part after the slash is the most specific identifier for
            // the point. However mods may append suffixes (e.g. _ExtendedHours,
            // _v2, _Patched) that don't exist in GameLocation.Name.
            //
            // We try the sub-name in increasingly normalised forms:
            //   a) Raw sub-name as-is          "FishShop_ExtendedHours"
            //   b) Stripped at first underscore "FishShop"
            //   c) SDV resolver on each form
            int slash = pointName.IndexOf('/');
            if (slash >= 0)
            {
                string sub    = pointName[(slash + 1)..];
                string prefix = pointName[..slash];

                if (!string.IsNullOrEmpty(sub))
                {
                    // a) Raw
                    Add(sub);
                    try { Add(Game1.getLocationFromName(sub)?.Name); } catch { }

                    // b) Strip at first underscore (removes mod suffixes)
                    int underscore = sub.IndexOf('_');
                    if (underscore > 0)
                    {
                        string stripped = sub[..underscore];
                        Add(stripped);
                        try { Add(Game1.getLocationFromName(stripped)?.Name); } catch { }
                    }
                }

                // Also include all locations from the prefix area.
                if (_areaToLocations.TryGetValue(prefix, out var prefixLocs))
                    foreach (var n in prefixLocs) Add(n);
            }

            // 3. LocationHierarchy children of each candidate so far.
            //    If "Beach" is a candidate, "ElliottHouse" (its child) is added.
            //    This handles hover points that represent building entrances.
            var snapshot = candidates.ToList();
            foreach (var loc in snapshot)
                foreach (var child in LocationHierarchy.GetChildren(loc))
                    Add(child);

            // 4. SDV resolver on the full point name.
            try { Add(Game1.getLocationFromName(pointName)?.Name); } catch { }

            // 5. Substring search across all known locations (last resort).
            //    Uses both the raw sub-name and the underscore-stripped form so
            //    "FishShop_ExtendedHours" finds "FishShop" as a candidate.
            string lower = pointName.ToLowerInvariant();
            string subLower = slash >= 0 ? pointName[(slash + 1)..].ToLowerInvariant() : lower;
            // Stripped: remove everything from first underscore onward.
            int ul = subLower.IndexOf('_');
            string subStrippedLower = ul > 0 ? subLower[..ul] : subLower;

            foreach (var locName in LocationHierarchy.AllLocations)
            {
                string locLower = locName.ToLowerInvariant();
                // Prefer matches against the stripped sub-name (most specific).
                if (!string.IsNullOrEmpty(subStrippedLower)
                    && (locLower.Contains(subStrippedLower) || subStrippedLower.Contains(locLower)))
                    Add(locName);
                else if (locLower.Contains(subLower) || subLower.Contains(locLower))
                    Add(locName);
                else if (lower.Contains(locLower) || locLower.Contains(lower))
                    Add(locName);
            }

            return candidates;
        }

        // ── Scoring ───────────────────────────────────────────────────────────
        private static string GetAreaPrefix(string pointName)
        {
            int slash = pointName.IndexOf('/');
            return slash > 0 ? pointName[..slash] : pointName;
        }
        private static bool InSameAreaScope(string pointName, string candidate)
        {
            string area = GetAreaPrefix(pointName);

            // enforce namespace rule first
            if (!candidate.Contains(area, StringComparison.OrdinalIgnoreCase))
            {
                // allow exceptions only for exact SDV matches
                return false;
            }

            return true;
        }
        private static string NormalizeLocation(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            name = name.Trim();

            // split into words (handles CamelCase + underscores + spaces)
            name = name.Replace("_", " ");

            // remove common building suffixes (NOT hardcoded per game logic, just structural noise)
            string[] suffixes =
            {
        "house",
        "cabin",
        "tower",
        "tent",
        "home",
        "shop",
"extendedhours"
            };

            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                             .Select(p => p.Trim());

            parts = parts.Where(p =>
                !suffixes.Contains(p, StringComparer.OrdinalIgnoreCase));

            return string.Join(" ", parts).Trim();
        }
        private static void Lock(string candidate)
        {
            _resolutionLocked.Add(candidate);
        }
        private static int Score(
            string candidate,
            string pointName,
            ForageTracker? tracker,
            List<string> allCandidates,
            HashSet<string> allCandidateSet)
        {
            /*
            if (_resolutionLocked.Contains(candidate))
                return int.MaxValue;*/
                int score = 0;
            var type  = LocationHierarchy.GetLocationType(candidate);
           
            string sub = pointName.Contains('/') ? pointName[(pointName.IndexOf('/') + 1)..] : pointName;

            bool isExactNormalizedMatch =  NormalizeLocation(candidate) .Equals(NormalizeLocation(sub), StringComparison.OrdinalIgnoreCase);

            if (isExactNormalizedMatch)
                score = Math.Max(score, 10000);


            _monitor?.Log($"[isExactNormalizedMatch] '{score}' ", LogLevel.Debug);
            string areaId = _locationToPoint.TryGetValue(pointName, out var a) ? a : null;

            if (areaId != null)
            {
                if (candidate.Equals(areaId, StringComparison.OrdinalIgnoreCase))
                    score += 20;
            }
            // ── Tracker relevance ─────────────────────────────────────────────
            if (tracker?.IsTracked(candidate) == true) score += 100;

            // ── Type specificity ──────────────────────────────────────────────
            // Indoor/building is more specific than an outdoor area.
            if (type == LocationType.IndoorMap || type == LocationType.FarmBuilding)
                score += 50;
            if (type == LocationType.Outdoor)
                score -= 25;

            // ── String evidence ───────────────────────────────────────────────
            // Slash sub-name matching. We test both the raw sub-name and the
            // underscore-stripped form so that "FishShop_ExtendedHours" correctly
            // scores "FishShop" higher than unrelated beach children.
            int slash = pointName.IndexOf('/');
            string subName = slash >= 0 ? pointName[(slash + 1)..] : pointName;

            // Stripped form: "FishShop_ExtendedHours" → "FishShop"
            int underscore = subName.IndexOf('_');
            string subStripped = underscore > 0 ? subName[..underscore] : subName;

            if (slash >= 0)
            {
                // Exact match on raw sub-name: strongest evidence.
                if (candidate.Equals(subName, StringComparison.OrdinalIgnoreCase))
                    score += 80;
                // Exact match on stripped sub-name: very strong evidence.
                else if (candidate.Equals(subStripped, StringComparison.OrdinalIgnoreCase))
                    score += 75;
                // Partial match on stripped form.
                else if (candidate.Contains(subStripped, StringComparison.OrdinalIgnoreCase)
                      || subStripped.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                    score += 35;
                // Partial match on raw form.
                else if (candidate.Contains(subName, StringComparison.OrdinalIgnoreCase)
                      || subName.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                    score += 25;
            }
            else
            {
                if (pointName.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                    score += 30;
                if (candidate.Contains(pointName, StringComparison.OrdinalIgnoreCase))
                    score += 20;
            }

            // ── Hierarchy penalty for overly broad ancestors ──────────────────
            // If this candidate is a parent/ancestor of another candidate in the
            // set, penalise it — we want the most specific (deepest) match.
            foreach (var other in allCandidates)
            {
                if (other.Equals(candidate, StringComparison.OrdinalIgnoreCase)) continue;
                // If candidate is an ancestor of 'other', penalise.
                string? p = LocationHierarchy.GetParent(other);
                while (p != null)
                {
                    if (p.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        score -= 100; // candidate is too broad
                        break;
                    }
                    p = LocationHierarchy.GetParent(p);
                }
            }

            // ── Child-of-area bonus ───────────────────────────────────────────
            // If this candidate is a direct child of one of the WorldPositions
            // locations for this point, it's highly relevant.
            if (_pointToLocations.TryGetValue(pointName, out var wpLocs))
            {
                string? parent = LocationHierarchy.GetParent(candidate);
                if (parent != null && wpLocs.Contains(parent, StringComparer.OrdinalIgnoreCase))
                    score += 60;
            }

            // ── Unambiguous bonus ─────────────────────────────────────────────
            if (allCandidates.Count == 1) score += 10;

            return score;
        }
    }
}
