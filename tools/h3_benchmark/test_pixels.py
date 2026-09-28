import unittest
from PIL import Image

from compare_pixels import difference


class PixelDifferenceTests(unittest.TestCase):
    def test_reports_exact_pixels_and_single_channel_change(self):
        a=Image.new("RGB",(2,2),(10,20,30))
        b=a.copy()
        self.assertTrue(difference(a,b)["exact"])
        b.putpixel((0,0),(13,20,30))
        result=difference(a,b)
        self.assertFalse(result["exact"])
        self.assertEqual(result["maximum_channel_difference"],3)
        self.assertEqual(result["mean_absolute_channel_difference"],.25)
        with self.assertRaises(ValueError):
            difference(a,Image.new("RGB",(3,3)))
