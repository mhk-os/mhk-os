using System;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>Voice line slots of a character.</summary>
    public enum CharacterVoice { Horn, Idle, Shout, Win, Lose, Hit }

    /// <summary>The clips of one voice slot (one is picked at random).</summary>
    [Serializable]
    public struct CharacterVoiceBinding
    {
        public CharacterVoice voice;
        public AudioClip[] clips;
    }
}
