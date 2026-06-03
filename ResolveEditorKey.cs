namespace ForageTrackerModSV
{
    public static class ResolveEditorKey
    {
        /// <summary>
        /// Resolves the editor map key associated with a live Stardew Valley map key.
        ///
        /// Region definitions are stored using editor-specific keys, while the game
        /// operates using live map keys. Bindings provide the relationship between
        /// the two systems. If no binding exists, the live key is returned unchanged.
        /// </summary>
        /// <param name="liveMapKey">
        /// The active Stardew Valley map key.
        /// </param>
        /// <param name="bindings">
        /// Mapping of editor map keys to live map keys.
        /// </param>
        /// <returns>
        /// The editor map key that should be used when retrieving region data.
        /// </returns>
        public static string Resolve(string liveKey, Dictionary<string, string> bindings)
        {
            foreach (var binding in bindings)
            {
                if (binding.Value == liveKey)  return binding.Key;
            }
            return liveKey;
        }
    }
}
