namespace MHK.Karts
{
    /// <summary>Anything that controls a kart. Called once per physics tick before the simulation step.</summary>
    public interface IKartDriver
    {
        /// <summary>Writes this tick's controls of <paramref name="kart"/> into <paramref name="input"/>.</summary>
        void UpdateInput(Kart kart, ref KartInput input);
    }
}
