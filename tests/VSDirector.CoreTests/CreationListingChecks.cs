using System.Diagnostics;
using System.Text.Json;
using VSDirector.Data;

internal static class CreationListingChecks
{
    internal static void Run()
    {
        static void Check(bool value,string message){if(!value)throw new Exception(message);}
        string root=Path.Combine(Path.GetTempPath(),"director-listing-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new DirectorCreationStore(root);
            var small=new DirectorCreationProject{Name="Small",WorldId="world",Dimension=1};store.Save(small);
            store.List(); // Warm serializer metadata before measuring.
            var large=new DirectorCreationProject{Name="Large",WorldId="world",Dimension=2};
            string directory=store.DirectoryFor(large.Id);Directory.CreateDirectory(directory);
            string file=Path.Combine(directory,"project.json");
            using(var writer=new StreamWriter(file))
            {
                writer.Write("{\"SchemaVersion\":2,\"Name\":\"Large\",\"WorldId\":\"world\",\"Dimension\":2,\"Tracks\":[{\"Keys\":[");
                for(int i=0;i<300000;i++){if(i>0)writer.Write(",");writer.Write("{\"Time\":1,\"Value\":{\"X\":1,\"Y\":2,\"Z\":3}}");}
                writer.Write("]}],\"Folder\":\"Films\",\"ModifiedUtc\":\"2026-11-14T00:00:00Z\"}");
            }
            long before=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();
            var entry=store.List().Single(e=>e.Id==large.Id);
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Console.WriteLine($"Project listing cold: {watch.ElapsedMilliseconds} ms, {allocated/1024} KiB allocated");
            Check(entry.Name=="Large"&&entry.Folder=="Films"&&entry.Dimension==2&&entry.WorldId=="world","Metadata after large animation payload lost");
            Check(allocated<8*1024*1024,"Listing materialized large animation payload");
            // A shared lock permits stat but prevents opening the payload again.
            using(var locked=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None))
                Check(store.List().Any(e=>e.Id==large.Id),"Unchanged project was reread instead of cached");
            large.Name="Renamed";store.Save(large);
            Check(store.List().Single(e=>e.Id==large.Id).Name=="Renamed","Save did not invalidate summary");
            large.Name="Recovery";store.Save(large,true);
            Check(store.List().Single(e=>e.Id==large.Id) is {Name:"Renamed",Recovery:true},"Recovery indication missing");
            File.Delete(file);
            Check(store.List().Single(e=>e.Id==large.Id) is {Name:"Recovery",Recovery:true},"Recovery-only listing missing");
            store.Delete(large.Id);Check(store.List().Count==1,"Deleted project remained cached");
            File.WriteAllText(Path.Combine(store.DirectoryFor(small.Id),"project.json"),"{");
            Check(store.List().Count==0,"Broken file retained stale entry");
        }
        finally {Directory.Delete(root,true);}
    }
}
