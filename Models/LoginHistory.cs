using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System;

namespace HeisenParserWPF.Models
{
    [Table("login_history")]
    public class LoginHistory : BaseModel
    {
        [Column("email")]
        public string Email { get; set; } = string.Empty;

        [Column("user_id")]
        public string UserId { get; set; } = string.Empty;

        [Column("login_time")]
        public DateTime LoginTime { get; set; } = DateTime.UtcNow;
    }
}
