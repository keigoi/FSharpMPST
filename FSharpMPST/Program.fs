type Either<'A,'B> = Left of 'A | Right of 'B

exception RoleNotEnabled

type SessionType<'A> =
  abstract member Merge : 'A -> 'A

type Branch<'ROLE, 'PAYLOAD> = Branch of 'ROLE * Lazy<List<'PAYLOAD>> with 
  interface SessionType<Branch<'ROLE, 'PAYLOAD>> with
    member this.Merge (Branch(_,ys)) =
      let (Branch(r1, xs)) = this in
      Branch (r1, lazy (xs.Force () @ (ys.Force())))

type Select<'ROLE, 'PAYLOAD> = Select of 'ROLE * Lazy<'PAYLOAD> with 
  interface SessionType<Select<'ROLE, 'PAYLOAD>> with
    member this.Merge _ = raise RoleNotEnabled

type Close = Close with
  interface SessionType<Close> with
    member this.Merge _ = Close

type MPST<'T> = MPST of Lazy<List<'T>>

type Lens<'v1, 'v2, 's1, 's2> = {
    get : 's1 -> 'v1;
    put : 's1 -> 'v2 -> 's2;
  }

let merge (xs : List<'A> when 'A :> SessionType<'A>) : 'A  =
  List.fold (fun x y -> x.Merge y) (List.head xs) (List.tail xs)

let mpst l = {
    get = (fun (MPST xs) -> merge (List.map l.get (xs.Force())));
    put = (fun (MPST xs) v' -> MPST (lazy ((List.map (fun x -> l.put x v') (xs.Force())))))
  }

type Channel<'a> = MailboxProcessor<'a>

type Msg<'A, 'CONT> = 
  Msg of MailboxProcessor<'A> * 'CONT 
type Left<'CONT> = 
  Left_ of MailboxProcessor<bool> * 'CONT
type Right<'CONT> = 
  Right_ of MailboxProcessor<bool> * 'CONT
type LeftOrRight<'CONTLEFT,'CONTRIGHT> = 
  | LeftOrRight of MailboxProcessor<bool> * 'CONTLEFT * 'CONTRIGHT
  | Left__ of  MailboxProcessor<bool> * 'CONTLEFT
  | Right__ of  MailboxProcessor<bool> * 'CONTRIGHT

let send_msg : 'V -> Select<'R, Msg<'V, 'S>> -> 'S = fun v (Select (_, m)) ->
  let (Msg(chan, cont)) = m.Force () in
  chan.Post(v);
  cont

let send_left : 'V -> Select<'R, Left<'S>> -> 'S = fun v (Select (_, m)) ->
  let (Left_(chan, cont)) = m.Force () in
  chan.Post(true);
  cont

let send_right : 'V -> Select<'R, Right<'S>> -> 'S = fun v (Select (_, m)) ->
  let (Right_(chan, cont)) = m.Force () in
  chan.Post(false);
  cont

let receive_msg : Branch<'R, Msg<'V, 'S>> -> Async<'V * 'S> = fun (Branch (_, ms)) ->
  match ms.Force() with
  | [Msg(chan, cont)] -> 
    async { 
      let! v = chan.Receive()
      return v, cont
    }
  | _ -> failwith "impossible: receive_msg"

// from: http://www.fssnip.net/dN/title/AsyncChoice
[<AutoOpen>]
module AsyncEx = 
    type private SuccessException<'T>(value : 'T) =
        inherit System.Exception()
        member self.Value = value

    type Microsoft.FSharp.Control.Async with
        // efficient raise
        static member Raise (e : #exn) = Async.FromContinuations(fun (_,econt,_) -> econt e)
  
        static member Choice<'T>(tasks : List<Async<'T>>) : Async<'T> =
            let wrap task =
                async {
                    let! res = task
                    return! Async.Raise <| SuccessException res
                }
            async {
                try
                    let! xs = tasks |> List.map wrap |> Async.Parallel
                    return (xs.[0])
                with 
                | :? SuccessException<'T> as ex -> return ex.Value
            }

let receive_either : Branch<'R, LeftOrRight<'S1, 'S2>> -> Async<Either<'S1, 'S2>> = fun (Branch (_, ms)) ->
  let ms = ms.Force() in
  let wrap = function
    | LeftOrRight(chan, l, r) ->
      async {
        let! b = chan.Receive()
        if b then
          return (Left l)
        else
          return (Right r)
      }
    | Left__(chan, l) ->
      async {
        let! b = chan.Receive()
        if b then
          return (Left l)
        else
          return (failwith "impossible: receive_either -- false received for left case")
      }
    | Right__(chan, r) ->
      async {
        let! b = chan.Receive()
        if b then
          return (failwith "impossible: receive_either -- false received for left case")
        else
          return (Right r)
      }
  in
  ms |> List.map wrap |> Async.Choice
  
let close : Close -> Unit = fun Close -> ()


let (==>) = fun (lens1, role1) (lens2, role2) cont ->
  let chan = new MailboxProcessor<'T>(fun _ -> async{return ()}) in
  let cont' = lens1.put cont (Select (role1, lazy (Msg(chan, lens1.get cont)))) in
  let cont'' = lens2.put cont' (Branch (role2, lazy [Msg(chan, lens2.get cont')])) in
  cont''

let (-%%->) = fun (lens1, role1) (lens2, role2) (((lensL1, _),(lensL2, _)), contLeft) (((lensR1, _),(lensR2, _)), contRight) ->
  let chan = new MailboxProcessor<bool>(fun _ -> async{return ()}) in
  let contLeft' = lensL1.put contLeft Close in
  let contLeft'' = lensL2.put contLeft' Close in
  let contRight' = lensR1.put contRight Close in
  let contRight'' = lensR2.put contRight' Close in
  let sel = Select (role1, lazy LeftOrRight (chan, lensL1.get contLeft, lensR1.get contRight)) in
  let bra = Branch (role2, lazy [LeftOrRight (chan, lensL2.get contLeft', lensR2.get contRight')]) in
  let (MPST contLeft''') = lens2.put (lens1.put contLeft'' sel) bra in
  let (MPST contRight''') = lens2.put (lens1.put contRight'' sel) bra in
  MPST (lazy (contLeft'''.Force() @ contRight'''.Force()))

type A = A
type B = B
type C = C

type RoleLens<'a, 'b, 'c, 'd, 'r> = Lens<'a, 'b, 'c, 'd>  * 'r

module ThreeParty = begin
  type PT3<'A,'B,'C> = PT3 of 'A * 'B * 'C
  let fst = {get=(fun (PT3(x,_,_)) -> x); put = (fun (PT3(_,y,z)) x' -> PT3(x',y,z))}
  let snd = {get=(fun (PT3(_,y,_)) -> y); put = (fun (PT3(x,_,z)) y' -> PT3(x,y',z))}
  let thd = {get=(fun (PT3(_,_,z)) -> z); put = (fun (PT3(x,y,_)) z' -> PT3(x,y,z'))}
  let a = {get = (fun s -> (mpst fst).get s); put = (fun s v' -> (mpst fst).put s v')}, A
  let b = {get = (fun s -> (mpst snd).get s); put = (fun s v' -> (mpst snd).put s v')}, B
  let c = {get = (fun s -> (mpst thd).get s); put = (fun s v' -> (mpst thd).put s v')}, C
  let finish = MPST (lazy [PT3 (Close, Close, Close)])
end


open ThreeParty


type Label1<'C1, 'C2, 'S, 'R> = 
  {sender : 'C1 -> 'S; receiver : 'C2 -> 'R}

let msg : Unit -> Label1<'C1, 'C2, Msg<'T, 'C1>, Msg<'T, 'C2>> = fun () ->
  let chan = new MailboxProcessor<'T>(fun _ -> async{return ()}) in
  {sender=(fun cont -> Msg(chan, cont));
  receiver=(fun cont -> Msg(chan, cont))}

let left : Unit -> Label1<'C1, 'C2, Left<'C1>, LeftOrRight<'C2, 'UNUSED>> = fun () ->
  let chan = new MailboxProcessor<bool>(fun _ -> async{return ()}) in
  {sender=(fun cont -> Left_(chan, cont));
  receiver=(fun cont -> Left__(chan, cont))}
  
let right : Unit -> Label1<'C1, 'C2, Right<'C1>, LeftOrRight<'UNUSED, 'C2>> = fun () ->
  let chan = new MailboxProcessor<bool>(fun _ -> async{return ()}) in
  {sender=(fun cont -> Right_(chan, cont));
  receiver=(fun cont -> Right__(chan, cont))}


let (-->) = fun (lens1, role1) (lens2, role2) label cont ->
  let cont' = lens1.put cont (Select (role1, lazy (label.sender (lens1.get cont)))) in
  let cont'' = lens2.put cont' (Branch (role2, lazy [label.receiver (lens2.get cont')])) in
  cont''

let (@@) f x = f x

let create_g () =
    (c --> a) (msg ()) @@
    (a -%%-> b)
      ((a,b),(b --> c) (right ()) @@
             (b --> a) (msg ()) @@
             finish)
      ((a,b),(b --> a) (msg ()) @@
             (b --> c) (left ()) @@
             (c --> a) (msg ()) @@
             finish)

[<EntryPoint>]
let main argv = 
    printfn "%A" argv
    0 // return an integer exit code
