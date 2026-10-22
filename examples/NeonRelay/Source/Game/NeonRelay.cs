using System;
using System.Linq;
using System.IO;
using FlaxEngine;
using FlaxEngine.GUI;

namespace Game
{
    public class NeonRelay : Script
    {
        public int Score { get; private set; }
        public int Health { get; private set; } = 3;
        public float Remaining { get; private set; } = 90;
        public string Phase { get; private set; } = "ready";
        public float Elapsed { get; private set; }
        public float DashCooldown { get; private set; }
        public int CollectedCount => Score;
        public int SoundEvents { get; private set; }
        public bool Grounded => player != null && player.IsGrounded;
        public Vector3 PlayerPosition => player?.Position ?? Vector3.Zero;
        CharacterController player;
        Actor[] cores, drones;
        Vector3[] coreStarts, droneStarts;
        bool[] collected;
        StaticModel body;
        AudioSource speaker;
        RelayHud hud;
        float vertical, immunity, dashTime, agentTime, axisX, axisZ;
        bool jumpRequest, dashRequest;
        Vector3 direction = Vector3.Forward;
        readonly Vector3 spawn = new Vector3(0, 100, -900);
        public override void OnStart()
        {
            player = Level.FindActor("Runner") as CharacterController;
            body = Level.FindActor("RunnerBody") as StaticModel;
            cores = Actor.Scene.Children.Where(x=>x.Name.StartsWith("Core_")).OrderBy(x=>x.Name).ToArray();
            drones = Actor.Scene.Children.Where(x=>x.Name.StartsWith("Drone_")).OrderBy(x=>x.Name).ToArray();
            coreStarts = cores.Select(x=>x.Position).ToArray(); droneStarts = drones.Select(x=>x.Position).ToArray(); collected = new bool[cores.Length];
            var canvas=Actor.AddChild<UICanvas>(); canvas.Name="Relay HUD"; canvas.RenderMode=CanvasRenderMode.CameraSpace; canvas.RenderCamera=Camera.MainCamera; canvas.Distance=700; canvas.IgnoreDepth=true;
            hud=new RelayHud(this); hud.Parent=canvas.GUI;
            speaker=Actor.AddChild<AudioSource>(); speaker.AllowSpatialization=false; speaker.Volume=0.35f;
            Reset(false);
        }
        void Sound(string name)
        {
            var clip=Content.LoadAsync<AudioClip>(Path.Combine(Globals.ProjectContentFolder,"Audio",name+".flax"));
            if(clip!=null){speaker.Clip=clip;speaker.Play();SoundEvents++;}
        }
        void Reset(bool start)
        {
            player.Position=spawn; vertical=0; Score=0; Health=3; Remaining=90; Elapsed=0; DashCooldown=0; dashTime=0; immunity=0; agentTime=0;axisX=axisZ=0;
            for(int i=0;i<cores.Length;i++){collected[i]=false;cores[i].IsActive=true;cores[i].Position=coreStarts[i];}
            Phase=start?"running":"ready";
        }
        public void StartRun(){Reset(true);}
        // Agent tests feed the same movement path as keyboard input, without teleporting.
        public void Drive(float x,float z,float seconds,bool jump=false,bool dash=false)
        {
            axisX=Mathf.Clamp(x,-1,1);axisZ=Mathf.Clamp(z,-1,1);agentTime=Mathf.Clamp(seconds,0,5);jumpRequest=jump;dashRequest=dash;
        }
        public override void OnUpdate()
        {
            float dt=Math.Min(Time.DeltaTime,0.05f);
            if(Input.GetKeyDown(KeyboardKeys.Return) && Phase!="running")StartRun();
            if(Input.GetKeyDown(KeyboardKeys.R))StartRun();
            for(int i=0;i<cores.Length;i++)if(!collected[i]) {cores[i].LocalEulerAngles+=new Vector3(0,70*dt,0);cores[i].Position=coreStarts[i]+new Vector3(0,(float)Math.Sin(Time.GameTime*2+i)*12,0);}
            if(Phase!="running")return;
            Elapsed+=dt;Remaining=Math.Max(0,Remaining-dt);DashCooldown=Math.Max(0,DashCooldown-dt);immunity=Math.Max(0,immunity-dt);
            float x=0,z=0;bool jump=false,dash=false;
            if(agentTime>0){agentTime-=dt;x=axisX;z=axisZ;jump=jumpRequest;dash=dashRequest;jumpRequest=dashRequest=false;}
            else { x=(Input.GetKey(KeyboardKeys.D)?1:0)-(Input.GetKey(KeyboardKeys.A)?1:0); z=(Input.GetKey(KeyboardKeys.W)?1:0)-(Input.GetKey(KeyboardKeys.S)?1:0);jump=Input.GetKeyDown(KeyboardKeys.Spacebar);dash=Input.GetKeyDown(KeyboardKeys.Shift); }
            var move=new Vector3(x,0,z);if(move.LengthSquared>1)move.Normalize();if(move.LengthSquared>0.01f)direction=move;
            if(jump && player.IsGrounded){vertical=650;Sound("jump");}
            if(dash && DashCooldown<=0){dashTime=0.2f;DashCooldown=1.5f;Sound("dash");}
            if(player.IsGrounded && vertical<0)vertical=-20;vertical-=1700*dt;
            if(dashTime>0){dashTime-=dt;move=direction*1100;immunity=Math.Max(immunity,0.05f);}else move*=420;
            player.Move(new Vector3(move.X,vertical,move.Z)*dt);
            if(body!=null){body.LocalEulerAngles=new Vector3(0,(float)Math.Atan2(direction.X,direction.Z)*Mathf.RadiansToDegrees,0);body.IsActive=immunity<=0 || ((int)(Elapsed*12)%2==0);}
            for(int i=0;i<drones.Length;i++)
            {
                float t=Elapsed*(0.7f+i*0.13f)+i*2;
                drones[i].Position=droneStarts[i]+new Vector3((float)Math.Sin(t)*430,0,(float)Math.Cos(t)*140);
                drones[i].LocalEulerAngles=new Vector3(0,Elapsed*100,0);
                if(immunity<=0 && Vector3.Distance(player.Position,drones[i].Position)<125){Health--;immunity=1.8f;Sound("hurt");if(Health<=0)Phase="lost";}
            }
            for(int i=0;i<cores.Length;i++)if(!collected[i] && Vector3.Distance(player.Position,cores[i].Position)<130){collected[i]=true;cores[i].IsActive=false;Score++;Sound("collect");}
            if(player.Position.Y < -300){Health--;player.Position=spawn;vertical=0;immunity=2;Sound("hurt");if(Health<=0)Phase="lost";}
            if(Remaining<=0)Phase="lost";
            if(Score==cores.Length && Vector3.Distance(player.Position,new Vector3(0,100,1000))<180){Phase="won";Sound("win");}
        }
        public override void OnDestroy(){hud?.Dispose();}
    }
    public class RelayHud : Control
    {
        readonly NeonRelay game;
        Font small, normal, title;
        readonly Color ink=new Color(0.025f,0.04f,0.09f,0.94f),cyan=new Color(0.15f,0.95f,1,1),white=new Color(0.92f,0.97f,1,1),muted=new Color(0.55f,0.68f,0.8f,1);
        public RelayHud(NeonRelay state){game=state;var font=Content.LoadAsync<FontAsset>(Path.Combine(Globals.EngineContentFolder,"Editor/Fonts/Roboto-Regular.flax"));font.WaitForLoaded();small=font.CreateFont(15);normal=font.CreateFont(24);title=font.CreateFont(48);}
        void Text(Font f,string text,float x,float y,Color c){Render2D.DrawText(f,text,c,new Float2(x,y));}
        public override void Draw()
        {
            if(Parent!=null)Size=Parent.Size;
            float w=Width,h=Height;
            Render2D.FillRectangle(new Rectangle(24,22,260,78),ink);Render2D.FillRectangle(new Rectangle(24,22,4,78),cyan);
            Text(small,"NEON RELAY  /  SECTOR 07",42,32,cyan);Text(normal,$"CORES   {game.Score} / 8",42,54,white);
            Render2D.FillRectangle(new Rectangle(w-238,22,214,78),ink);Text(small,"SUIT INTEGRITY",w-218,32,muted);
            for(int i=0;i<3;i++)Render2D.FillRectangle(new Rectangle(w-218+i*59,60,47,14),i<game.Health?cyan:new Color(.2f,.25f,.3f,1));
            Render2D.FillRectangle(new Rectangle(w/2-64,22,128,58),ink);Text(normal,$"{(int)Math.Ceiling(game.Remaining):00}s",w/2-27,35,white);
            Render2D.FillRectangle(new Rectangle(24,h-66,w-48,42),ink);
            Text(small,"WASD  MOVE     SPACE  JUMP     SHIFT  DASH     R  RESTART",42,h-54,muted);
            Text(small,game.Score==8?"ALL CORES ONLINE  >  REACH THE GATE":"COLLECT 8 CORES. AVOID THE DRONES.",42,h-99,cyan);
            Render2D.FillRectangle(new Rectangle(25,h-77,180*(1-game.DashCooldown/1.5f),4),cyan);
            if(game.Phase!="running")
            {
                Render2D.FillRectangle(new Rectangle(0,0,w,h),new Color(0.01f,0.02f,0.05f,.72f));
                float left=w/2-310,top=h/2-155;
                Render2D.FillRectangle(new Rectangle(left,top,620,310),ink);Render2D.FillRectangle(new Rectangle(left,top,620,3),cyan);
                Text(small,"A SIGNAL IN THE DARK",left+36,top+25,cyan);
                Text(title,game.Phase=="won"?"RELAY RESTORED":game.Phase=="lost"?"SIGNAL LOST":"NEON RELAY",left+34,top+58,white);
                Text(normal,game.Phase=="ready"?"Eight cores. Three drones. One way out.":game.Phase=="won"?$"Sector secured in {(int)game.Elapsed} seconds.":"Your next run starts here.",left+36,top+128,muted);
                Text(small,"Collect the floating cores, then return to the far gate.",left+36,top+177,white);
                Text(small,"Jump over obstacles. Dash through danger.",left+36,top+202,muted);
                Render2D.FillRectangle(new Rectangle(left+36,top+245,548,42),cyan);Text(normal,"PRESS ENTER TO RUN",left+170,top+251,ink);
            }
        }
    }
}
