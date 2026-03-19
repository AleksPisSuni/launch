using System;
using System.Threading.Tasks;

namespace LaunchpadMapper.Services
{
    public interface ITtsService : IDisposable
    {
        Task<string?> SynthesizeToFileAsync(string id, string text);
        void SetVoiceById(string? voiceId);
    }
}
