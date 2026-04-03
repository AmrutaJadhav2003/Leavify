using Microsoft.AspNetCore.Mvc;

namespace Rite.LeaveManagement.Svc.Models
{
    public class ProfileUploadRequest
    {
        [FromForm(Name = "jwtToken")]
        public string JwtToken { get; set; } = null!;

        [FromForm(Name = "profileImage")]
        public IFormFile ProfileImage { get; set; } = null!;
    }
}
