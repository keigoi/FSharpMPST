module Chan
open System

type Chan<'T> = Chan of MailboxProcessor<'T> * Threading.CancellationTokenSource

let create () = 
  let cts = new Threading.CancellationTokenSource() in
  let mbp = new MailboxProcessor<'T>((fun _ -> async{return ()}), cts.Token) in
  mbp.Start();
  Chan(mbp, cts)
let push (Chan (c,_)) v = c.Post v
let wait (Chan (c,_)) = c.Receive()
