namespace MHK.Core
{
    /// <summary>Layer indices of ProjectSettings/TagManager and the masks built from them.</summary>
    public static class GameLayers
    {
        public const int Default = 0;
        public const int UI = 5;
        public const int Kart = 8;
        public const int Track = 9;
        public const int Item = 10;
        public const int Hidden = 11;
        /// <summary>The main menu's own 3D objects (showcase, fade quad), drawn only by the menu camera.</summary>
        public const int Menu = 12;

        public const int UIMask = 1 << UI;
        public const int KartMask = 1 << Kart;
        public const int TrackMask = 1 << Track;
        public const int ItemMask = 1 << Item;
        public const int KartAndItemMask = KartMask | ItemMask;
        public const int MenuMask = 1 << Menu;
    }
}
