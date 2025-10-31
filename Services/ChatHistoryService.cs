using HeisenParserWPF.Pages; // Required to access ChatSession class
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace HeisenParserWPF.Services
{
    public static class ChatHistoryService
    {
        private static readonly string _filePath;
        private static Dictionary<string, ObservableCollection<ChatSession>> _allUserHistories;

        static ChatHistoryService()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolderPath = Path.Combine(appDataPath, "HeisenParserWPF");
            Directory.CreateDirectory(appFolderPath);
            _filePath = Path.Combine(appFolderPath, "chat_history.json");

            LoadHistoryFromFile();
        }

        private static void LoadHistoryFromFile()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    _allUserHistories = JsonSerializer.Deserialize<Dictionary<string, ObservableCollection<ChatSession>>>(json);
                }
            }
            catch (Exception)
            {
                _allUserHistories = null; // Handle potential file corruption
            }
            finally
            {
                if (_allUserHistories == null)
                {
                    _allUserHistories = new Dictionary<string, ObservableCollection<ChatSession>>();
                }
            }
        }

        /// <summary>
        /// Saves the entire chat history for all users to the file.
        /// </summary>
        private static void SaveHistoryToFile()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_allUserHistories, options);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception)
            {
                // In a real app, add logging here
            }
        }

        /// <summary>
        /// Gets the chat history for a specific user.
        /// </summary>
        public static ObservableCollection<ChatSession> GetChatHistory(string userId)
        {
            // Use a default key for guests (users who are not logged in)
            string key = string.IsNullOrEmpty(userId) ? "guest_user" : userId;

            if (!_allUserHistories.ContainsKey(key))
            {
                _allUserHistories[key] = new ObservableCollection<ChatSession>();
            }
            return _allUserHistories[key];
        }

        /// <summary>
        /// Saves the provided chat history for a specific user and persists it to the file.
        /// </summary>
        public static void SaveChatHistory(string userId, ObservableCollection<ChatSession> history)
        {
            string key = string.IsNullOrEmpty(userId) ? "guest_user" : userId;
            _allUserHistories[key] = history;
            SaveHistoryToFile();
        }
    }
}