using System.Globalization;

namespace VSDirector.Data;

/// <summary>Pure, bounded expression evaluator. No script runtime, reflection or external state.</summary>
public sealed class ParticleExpression
{
    private static readonly Dictionary<string,ParticleExpression> cache=new(StringComparer.Ordinal);
    private readonly Node root;
    private ParticleExpression(Node root)=>this.root=root;
    public static ParticleExpression Compile(string text)
    {
        if(text is null||text.Length>4096)throw new InvalidDataException("Expression exceeds 4096 characters.");
        lock(cache)
        {
            if(cache.TryGetValue(text,out var value))return value;
            var parser=new Parser(text);value=new(parser.Parse());
            if(cache.Count>=512)cache.Clear();cache[text]=value;return value;
        }
    }
    public double Evaluate(ParticleEvaluation context)
    {int budget=512;double result=root.Eval(context,ref budget);if(!double.IsFinite(result))throw new InvalidDataException("Expression result is not finite.");return result;}
    public static readonly string[] Variables=["time","emitterTime","age","life","vx","vy","vz","speed","seed","windX","windY","windZ","temperature","rain","pi"];
    public static readonly string[] Functions=["sin","cos","tan","abs","sqrt","floor","ceil","min","max","pow","clamp","lerp","random","noise"];
    private sealed record Node(string Op,double Value=0,Node? A=null,Node? B=null,Node? C=null)
    {
        public double Eval(ParticleEvaluation c,ref int budget)
        {
            if(--budget<0)throw new InvalidDataException("Expression operation budget exceeded.");
            if(Op=="number")return Value;
            if(A is null)return Op switch {"time"=>c.Time,"emitterTime"=>c.EmitterTime,"age"=>c.Age,"life"=>c.Life,
                "vx"=>c.Velocity.X,"vy"=>c.Velocity.Y,"vz"=>c.Velocity.Z,"speed"=>c.Velocity.Float().Length(),"seed"=>c.Seed,
                "windX"=>c.Wind.X,"windY"=>c.Wind.Y,"windZ"=>c.Wind.Z,"temperature"=>c.Temperature,"rain"=>c.Rain,"pi"=>Math.PI,_=>throw new InvalidDataException("Unknown variable: "+Op)};
            double a=A.Eval(c,ref budget);
            if(Op=="?")return (a!=0?B!:C!).Eval(c,ref budget);
            if(Op=="&&"&&a==0)return 0;if(Op=="||"&&a!=0)return 1;
            double b=B?.Eval(c,ref budget)??0,d=C?.Eval(c,ref budget)??0;
            return Op switch {"+"=>a+b,"-"=>a-b,"*"=>a*b,"/"=>a/b,"%"=>a%b,"neg"=>-a,"!"=>a==0?1:0,
                "<"=>a<b?1:0,">"=>a>b?1:0,"<="=>a<=b?1:0,">="=>a>=b?1:0,"=="=>a==b?1:0,"!="=>a!=b?1:0,"&&"=>b!=0?1:0,"||"=>b!=0?1:0,
                "sin"=>Math.Sin(a),"cos"=>Math.Cos(a),"tan"=>Math.Tan(a),"abs"=>Math.Abs(a),"sqrt"=>Math.Sqrt(a),"floor"=>Math.Floor(a),"ceil"=>Math.Ceiling(a),
                "min"=>Math.Min(a,b),"max"=>Math.Max(a,b),"pow"=>Math.Pow(a,b),"clamp"=>Math.Clamp(a,b,d),"lerp"=>a+(b-a)*d,
                "random"=>Hash(a+c.Seed),"noise"=>Noise(a,c.Seed),_=>throw new InvalidDataException("Unknown operation: "+Op)};
        }
        private static double Hash(double v){double n=Math.Sin(v*12.9898+78.233)*43758.5453;return n-Math.Floor(n);}
        private static double Noise(double v,double seed){double i=Math.Floor(v),t=v-i;t=t*t*(3-2*t);return Hash(i+seed)*(1-t)+Hash(i+1+seed)*t;}
    }
    private sealed class Parser(string source)
    {
        private int pos,nodes,depth;
        private Node Make(string op,double value=0,Node? a=null,Node? b=null,Node? c=null)
        {if(++nodes>512)throw Error("Too many operations");return new(op,value,a,b,c);}
        private InvalidDataException Error(string message)=>new($"{message} at column {pos+1}.");
        private void Space(){while(pos<source.Length&&char.IsWhiteSpace(source[pos]))pos++;}
        private bool Take(string s){Space();if(source.AsSpan(pos).StartsWith(s,StringComparison.Ordinal)){pos+=s.Length;return true;}return false;}
        public Node Parse(){var n=Expression();Space();if(pos!=source.Length)throw Error("Unexpected token");return n;}
        private Node Expression(int minimum=0)
        {
            if(++depth>64)throw Error("Expression nesting exceeds 64 levels");
            var a=Atom();
            while(true)
            {
                Space();string? op=null;int priority=0;
                foreach(var pair in new[]{("||",1),("&&",2),("==",3),("!=",3),("<=",4),(">=",4),("<",4),(">",4),("+",5),("-",5),("*",6),("/",6),("%",6)})
                    if(source.AsSpan(pos).StartsWith(pair.Item1,StringComparison.Ordinal)){op=pair.Item1;priority=pair.Item2;break;}
                if(op is null||priority<minimum)break;pos+=op.Length;a=Make(op,a:a,b:Expression(priority+1));
            }
            if(minimum==0&&Take("?")){var b=Expression();if(!Take(":"))throw Error("Expected :");a=Make("?",a:a,b:b,c:Expression());}
            depth--;return a;
        }
        private Node Atom()
        {
            Space();if(Take("-"))return Make("neg",a:Expression(7));if(Take("+"))return Expression(7);if(Take("!"))return Make("!",a:Expression(7));
            if(Take("(")){var a=Expression();if(!Take(")"))throw Error("Expected )");return a;}
            int start=pos;
            if(pos<source.Length&&(char.IsDigit(source[pos])||source[pos]=='.'))
            {
                while(pos<source.Length&&(char.IsDigit(source[pos])||source[pos]=='.'))pos++;
                if(pos<source.Length&&source[pos] is 'e' or 'E'){pos++;if(pos<source.Length&&source[pos] is '+' or '-')pos++;while(pos<source.Length&&char.IsDigit(source[pos]))pos++;}
                if(!double.TryParse(source[start..pos],NumberStyles.Float,CultureInfo.InvariantCulture,out double v)||!double.IsFinite(v))throw Error("Invalid number");return Make("number",v);
            }
            while(pos<source.Length&&(char.IsLetterOrDigit(source[pos])||source[pos]=='_'))pos++;
            string name=source[start..pos];if(name.Length==0)throw Error("Expected value");
            if(!Take("(")){if(!Variables.Contains(name))throw Error("Unknown variable "+name);return Make(name);}
            if(!Functions.Contains(name))throw Error("Unknown function "+name);
            int count=name is "clamp" or "lerp"?3:name is "min" or "max" or "pow"?2:1;
            var args=new List<Node>{Expression()};while(Take(","))args.Add(Expression());
            if(!Take(")")||args.Count!=count)throw Error("Invalid arguments for "+name);
            return Make(name,a:args[0],b:args.Count>1?args[1]:null,c:args.Count>2?args[2]:null);
        }
    }
}
