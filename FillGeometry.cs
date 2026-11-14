#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using UnityEngine;
namespace StutterFix
{
    // Unity CPU projection uses homogeneous -w..w clip coordinates (not GPU-adjusted z).
    internal static class FillGeometry
    {
        static float Distance(Vector4 p,int plane)
        {
            switch(plane) {case 0:return p.w+p.x;case 1:return p.w-p.x;case 2:return p.w+p.y;
                case 3:return p.w-p.y;case 4:return p.w+p.z;default:return p.w-p.z;}
        }
        internal static List<Vector2> Clip(Vector4 a,Vector4 b,Vector4 c)
        {
            var input=new List<Vector4>{a,b,c};
            for(int plane=0;plane<6 && input.Count>0;plane++)
            {
                var output=new List<Vector4>();var prev=input[input.Count-1];float dp=Distance(prev,plane);
                foreach(var p in input)
                {
                    float d=Distance(p,plane);
                    if((dp>=0)!=(d>=0))output.Add(prev+(p-prev)*(dp/(dp-d)));
                    if(d>=0)output.Add(p);prev=p;dp=d;
                }
                input=output;
            }
            var points=new List<Vector2>();foreach(var p in input)if(p.w>1e-8f)points.Add(new Vector2(p.x/p.w,p.y/p.w));
            return points;
        }
        internal static double Area(List<Vector2> p)
        {
            double sum=0;for(int i=0;i<p.Count;i++) {var a=p[i];var b=p[(i+1)%p.Count];sum+=(double)a.x*b.y-(double)a.y*b.x;}
            return Math.Abs(sum)*0.5;
        }
        static double Cross(Vector2 a,Vector2 b,Vector2 c)=>(double)(b.x-a.x)*(c.y-a.y)-(double)(b.y-a.y)*(c.x-a.x);
        internal static List<Vector2> Hull(List<Vector2> p)
        {
            p.Sort((a,b)=>a.x==b.x?a.y.CompareTo(b.y):a.x.CompareTo(b.x));
            var h=new List<Vector2>();
            foreach(var v in p) {while(h.Count>=2 && Cross(h[h.Count-2],h[h.Count-1],v)<=0)h.RemoveAt(h.Count-1);h.Add(v);}
            int lower=h.Count;for(int i=p.Count-2;i>=0;i--) {while(h.Count>lower && Cross(h[h.Count-2],h[h.Count-1],p[i])<=0)h.RemoveAt(h.Count-1);h.Add(p[i]);}
            if(h.Count>1)h.RemoveAt(h.Count-1);return h;
        }
    }
}
#endif
