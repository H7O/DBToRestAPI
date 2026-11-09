using Com.H.Cache;

namespace DBToRestAPI.Cache
{
    public class CacheInfo
    {
        public TimeSpan Duration { get; set; }
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// The largest answer the entry may hold, in bytes: the route's <c>max_entry_size_in_bytes</c>
        /// (see <see cref="CacheService.DefaultMaxEntrySize"/>).
        /// </summary>
        public long MaxEntrySize { get; set; } = CacheService.DefaultMaxEntrySize;
    }
}
