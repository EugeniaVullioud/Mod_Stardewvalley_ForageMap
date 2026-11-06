using ForageTrackerMod;

namespace ForageTrackerModSV
{
    /// <summary>
    /// Represents a location name together with its original and normalized forms.
    /// </summary>
    public sealed class LocationNameEntry
    {
        /// <summary>
        /// Gets or sets the original location name as registered in the game.
        /// </summary>
        public string OriginalName { get; set; } = "";
        /// <summary>
        /// Gets or sets the normalized location name used for matching and comparison.
        /// </summary>
        public string NormalizedName { get; set; } = "";
    }
    /// <summary>
    /// Maintains an index of location names, allowing lookups by both original and normalized names without repeatedly normalizing strings.
    /// Multiple original locations can share the same normalized name.
    /// </summary>
    public sealed class LocationNameIndex
    {
        /// <summary>
        /// Maps each original location name to its corresponding indexed entry. Uses case-insensitive name comparison.
        /// </summary>
        readonly Dictionary<string, LocationNameEntry> _byOriginal = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Maps each normalized location name to all original entries that produce that normalized value.
        /// Uses case-insensitive name comparison.
        /// </summary>
        readonly Dictionary<string, List<LocationNameEntry>> _byNormalized = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the total number of unique original location names indexed.
        /// </summary>
        public int Count => _byOriginal.Count;

        /// <summary>
        /// Determines whether an original location name is already indexed.
        /// </summary>
        public bool ContainsOriginal(string name)
        {
            return _byOriginal.ContainsKey(name);
        }
        /// <summary>
        /// Determines whether at least one location has the specified normalized name.
        /// </summary>
        public bool ContainsNormalized(string normalized)
        {
            return _byNormalized.ContainsKey(normalized);
        }
        /// <summary>
        /// Adds a location name to the index, storing both its original and normalized forms.
        /// Existing original names are ignored to prevent duplicate entries.
        /// </summary>
        public void Add(string originalName)
        {
            if (string.IsNullOrWhiteSpace(originalName)) return;

            originalName = originalName.Trim();

            // Already indexed.
            if (_byOriginal.ContainsKey(originalName)) return;

            string normalized = MapPointResolver.NormalizeLocation(originalName);

            if (string.IsNullOrWhiteSpace(normalized)) return;

            var entry = new LocationNameEntry
            {
                OriginalName = originalName,
                NormalizedName = normalized
            };

            _byOriginal.Add(originalName, entry);

            if (!_byNormalized.TryGetValue(normalized, out var entries))
            {
                entries = new List<LocationNameEntry>();
                _byNormalized[normalized] = entries;
            }

            entries.Add(entry);
        }
        /// <summary>
        /// Adds multiple original location names to the index. Duplicate names and invalid entries are ignored.
        /// </summary>
        public void AddRange(IEnumerable<string> names)
        {
            foreach (var name in names) Add(name);
        }
        /// <summary>
        /// Retrieves an indexed location entry using its original name.
        /// </summary>
        public LocationNameEntry? GetOriginal(string name)
        {
            return _byOriginal.TryGetValue(name, out var entry) ? entry : null;
        }
        /// <summary>
        /// Retrieves all location entries sharing the specified normalized name. This allows multiple original locations to match the same normalized value.
        /// The returned collection cannot be used to modify the internal index.
        /// </summary>
        public IReadOnlyList<LocationNameEntry> FindNormalized(string normalized)
        {
            return _byNormalized.TryGetValue(normalized, out var entries) ? entries.AsReadOnly() : Array.Empty<LocationNameEntry>();
        }
        /// <summary>
        /// Retrieves all currently indexed location entries.
        /// The returned collection cannot be used to modify the internal index.
        /// </summary>
        public IReadOnlyList<LocationNameEntry> GetAll()
        {
            return _byOriginal.Values.ToList().AsReadOnly();
        }
        /// <summary>
        /// Removes a location from the index using its original name. Also removes the entry from its corresponding normalized-name group.
        /// </summary>
        public bool Remove(string originalName)
        {
            if (!_byOriginal.Remove(originalName, out var entry)) return false;

            if (_byNormalized.TryGetValue(entry.NormalizedName, out var entries))
            {
                entries.Remove(entry);
                if (entries.Count == 0) _byNormalized.Remove(entry.NormalizedName);
            }
            return true;
        }
        /// <summary>
        /// Removes all indexed location entries and clears both lookup dictionaries.
        /// </summary>
        public void Clear()
        {
            _byOriginal.Clear();
            _byNormalized.Clear();
        }
    }
}