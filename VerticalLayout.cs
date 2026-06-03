using Microsoft.Xna.Framework;

namespace ForageTrackerModSV
{
    public struct SidebarRow
    {
        public Rectangle LabelRect;
        public Rectangle ControlRect;
    }
    
    sealed class VerticalLayout
    {
        private readonly int _x;
        private readonly int _width;
        private readonly int _spacing;

        public int Y { get; private set; }

        public VerticalLayout(
            int x,
            int startY,
            int width,
            int spacing)
        {
            _x = x;
            _width = width;
            _spacing = spacing;
            Y = startY;
        }

        public Rectangle Next(int height)
        {
            Rectangle rect = new Rectangle(_x,Y, _width, height);

            Y += height + _spacing;

            return rect;
        }

        public void Skip(int pixels)
        {
            Y += pixels;
        }
    }
}
