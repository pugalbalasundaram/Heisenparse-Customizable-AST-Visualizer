using Heisenparse2._0.Models;
using HeisenParserWPF.Models; // Ensure this using statement points to your model
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HeisenParserWPF.Services
{
    public static class RecentActivityService
    {
        private static readonly string _filePath;
        private static Dictionary<string, List<RecentActivity>> _allUserActivities;

        static RecentActivityService()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolderPath = Path.Combine(appDataPath, "HeisenParserWPF");
            Directory.CreateDirectory(appFolderPath); // Ensures the directory exists
            _filePath = Path.Combine(appFolderPath, "user_activities.json");

            LoadActivitiesFromFile();
        }

        private static void LoadActivitiesFromFile()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    _allUserActivities = JsonConvert.DeserializeObject<Dictionary<string, List<RecentActivity>>>(json);
                }
            }
            catch (Exception)
            {
                _allUserActivities = null; // In case of a corrupted file
            }

            // If loading fails or the file doesn't exist, initialize a new dictionary.
            if (_allUserActivities == null)
            {
                _allUserActivities = new Dictionary<string, List<RecentActivity>>();
            }
        }

        private static void SaveActivitiesToFile()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_allUserActivities, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception)
            {
                // In a production app, you would add error logging here.
            }
        }

        /// <summary>
        /// Gets recent activities for a specific user.
        /// </summary>
        public static List<RecentActivity> GetRecentActivities(string userId)
        {
            if (string.IsNullOrEmpty(userId) || !_allUserActivities.ContainsKey(userId))
            {
                return new List<RecentActivity>(); // Return an empty list if user is not found
            }
            return _allUserActivities[userId];
        }

        /// <summary>
        /// Adds or updates a recent activity for a specific user.
        /// </summary>
        public static void AddOrUpdateActivity(string userId, string filePath)
        {
            if (string.IsNullOrEmpty(userId)) return; // Don't track activities for guests

            if (!_allUserActivities.ContainsKey(userId))
            {
                _allUserActivities[userId] = new List<RecentActivity>();
            }

            var userActivities = _allUserActivities[userId];
            var existingActivity = userActivities.FirstOrDefault(a => a.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

            if (existingActivity != null)
            {
                // If it exists, just update the timestamp
                existingActivity.LastModified = DateTime.Now;
            }
            else
            {
                // If it's new, add it to the list
                userActivities.Add(new RecentActivity
                {
                    FilePath = filePath,
                    LastModified = DateTime.Now
                });
            }

            // Keep the list tidy by limiting to the 10 most recent items
            const int maxItems = 10;
            _allUserActivities[userId] = userActivities.OrderByDescending(a => a.LastModified).Take(maxItems).ToList();

            SaveActivitiesToFile();
        }
    }
}