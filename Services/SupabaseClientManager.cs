using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Supabase;

namespace HeisenParserWPF.Services
{
    public static class SupabaseClientManager
    {
        private static Supabase.Client? _client;
        private static readonly object _lock = new();

        private const string SUPABASE_URL = "https://djfembknpavwtwaieqap.supabase.co";
        private const string SUPABASE_KEY = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImRqZmVtYmtucGF2d3R3YWllcWFwIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NjA1NjMyMTQsImV4cCI6MjA3NjEzOTIxNH0.PCvYc_Kf4CvlUONWCUb_obauaYjPIxoR1ep-CEjx-Z0";

        public static Supabase.Client? Client => _client;

        public static async Task InitializeAsync()
        {
            if (_client != null)
                return;

            lock (_lock)
            {
                if (_client != null)
                    return;

                var options = new SupabaseOptions
                {
                    AutoConnectRealtime = false,
                    AutoRefreshToken = true
                };

                _client = new Supabase.Client(SUPABASE_URL, SUPABASE_KEY, options);
            }

            await _client.InitializeAsync();
        }

        public static bool IsInDesignMode()
        {
            return DesignerProperties.GetIsInDesignMode(new System.Windows.DependencyObject());
        }
    }
}
