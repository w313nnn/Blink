using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

class MotionChecks {
    static Bitmap Pose(int size,EyeExpression expression,bool waiting) {
        var result=new Bitmap(size,size,PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(result)) {
            g.Clear(Color.Transparent);
            PetArtwork.DrawAnimated(g,size,Color.FromArgb(165,200,243),waiting,false,false,false,13,0,0,expression);
        }
        return result;
    }
    static void CheckEyes(int size) {
        var eyes=new HashSet<int>();
        using(var neutral=Pose(size,EyeExpression.Neutral,false)) {
            foreach(EyeExpression mood in new[]{EyeExpression.Neutral,EyeExpression.Happy,EyeExpression.Sad,EyeExpression.Confused,EyeExpression.Exhausted,EyeExpression.Surprised}) {
                using(var frame=Pose(size,mood,false)) {
                    int signature=19;
                    for(int y=0;y<size;y++) for(int x=0;x<size;x++) {
                        int value=frame.GetPixel(x,y).ToArgb();
                        if(x>size*.13 && x<size*.85 && y>size*.25 && y<size*.70) {
                            signature=unchecked(signature*31+value);
                        } else if (neutral.GetPixel(x,y).ToArgb()!=value) {
                            throw new Exception(mood+" changed pixels outside the eye region");
                        }
                    }
                    if(!eyes.Add(signature))throw new Exception(mood+" reused another expression's eyes");
                }
                using(var waiting=Pose(size,mood,true)) {
                    for(int y=(int)(size*.665);y<(int)(size*.745);y++)
                        for(int x=(int)(size*.445);x<(int)(size*.535);x++)
                            if(waiting.GetPixel(x,y)!=neutral.GetPixel(x,y))
                                throw new Exception(mood+" changed the lower center of the face when waiting");
                }
            }
        }
        Console.WriteLine("PASS: six distinct eye expressions and a clear lower face at "+size+" pixels.");
    }
    static void Main() {
        CheckEyes(44);
        CheckEyes(108);
        foreach (int size in new[] { 44,55,81,108 }) {
            var signatures = new HashSet<int>();
            using (var bitmap = new Bitmap(size,size,PixelFormat.Format32bppPArgb))
            using (var graphics = Graphics.FromImage(bitmap)) {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                for (int i=0; i<24; i++) {
                    graphics.Clear(Color.Transparent);
                    PetArtwork.DrawAnimated(graphics,size,Color.SeaGreen,true,false,false,false,13,i/24.0,i/24.0);
                    graphics.Flush();
                    if (bitmap.GetPixel(0,0).A != 0 || bitmap.GetPixel(size/2,size/2).A < 240)
                        throw new Exception("Animation lost the transparent silhouette or solid face.");
                    int signature=17, redPixels=0;
                    for (int y=0; y<size; y++) {
                        for (int x=0; x<size; x++) {
                            Color pixel=bitmap.GetPixel(x,y);
                            signature=unchecked(signature*31+pixel.ToArgb());
                            if (pixel.A>190 && pixel.R>180 && pixel.G<110 && pixel.B<140) redPixels++;
                        }
                    }
                    if (redPixels<6) throw new Exception("A waiting frame lost its message badge.");
                    signatures.Add(signature);
                }
            }
            if (signatures.Count<6) throw new Exception("Blink animation jumped straight between poses.");
            Console.WriteLine("PASS: translucent animated edges, solid face and persistent badge at "+size+" pixels; "+signatures.Count+" distinct frames.");
        }
    }
}
