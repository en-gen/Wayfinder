namespace Wayfinder.Grains.Interfaces.Model
{
    public partial class CmmnElement
    {
        public override bool Equals(object obj)
        {
            if (!(obj is CmmnElement)) return false;
            return Id == ((CmmnElement)obj).Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }
}
