open System.Collections.Concurrent

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

type Channel<'a> = ConcurrentQueue<'a>

type Msg<'A, 'CONT> = Msg of ConcurrentQueue<'A> * 'CONT 
type Left<'A, 'CONT> = Left_ of 'A
type Right<'B, 'CONT> = Right_ of 'B
type LeftOrRight<'CONTLEFT,'CONTRIGHT> = LeftOrRight of ConcurrentQueue<bool> * 'CONTLEFT * 'CONTRIGHT

let send_msg : 'V -> Select<'R, Msg<'V, 'S>> -> 'S = fun v (Select (r, m)) ->
  let (Msg(chan, cont)) = m.Force () in
  chan.Enqueue(v);
  cont

let (-->) = fun (lens1, role1) (lens2, role2) cont ->
  let chan = new ConcurrentQueue<'T>() in
  let cont' = lens1.put cont (Select (role1, lazy (Msg(chan, lens1.get cont)))) in
  let cont'' = lens2.put cont' (Branch (role2, lazy [Msg(chan, lens2.get cont')])) in
  cont''

let (-%%->) = fun (lens1, role1) (lens2, role2) ((lensL1, _),(lensL2, _), contLeft) ((lensR1, _),(lensR2, _), contRight) ->
  let chan = new ConcurrentQueue<bool>() in
  let contLeft' = lensL1.put contLeft Close in
  let contLeft'' = lensL2.put contLeft' Close in
  let contRight' = lensR1.put contRight Close in
  let contRight'' = lensR2.put contRight' Close in
  let sel = Select (role1, lazy LeftOrRight (chan, lensL1.get contLeft, lensR2.get contLeft')) in
  let bra = Branch (role2, lazy [LeftOrRight (chan, lensL2.get contLeft', lensR2.get contRight')]) in
  let (MPST contLeft''') = lens2.put (lens1.put contLeft'' sel) bra in
  let (MPST contRight''') = lens2.put (lens1.put contRight'' sel) bra in
  MPST (lazy (contLeft'''.Force() @ contRight'''.Force()))


type A = A
type B = B
type C = C

type RoleLens<'a, 'b, 'c, 'd, 'r> = Lens<'a, 'b, 'c, 'd>  * 'r

module ThreeParty = struct
  type PT3<'A,'B,'C> = PT3 of 'A * 'B * 'C
  let fst = {get=(fun (PT3(x,_,_)) -> x); put = (fun (PT3(_,y,z)) x' -> PT3(x',y,z))}
  let snd = {get=(fun (PT3(_,y,_)) -> y); put = (fun (PT3(x,_,z)) y' -> PT3(x,y',z))}
  let thd = {get=(fun (PT3(_,_,z)) -> z); put = (fun (PT3(x,y,_)) z' -> PT3(x,y,z'))}
  let a = {get = (fun s -> (mpst fst).get s); put = (fun s v' -> (mpst fst).put s v')}, A
  let b = {get = (fun s -> (mpst snd).get s); put = (fun s v' -> (mpst snd).put s v')}, B
  let c = {get = (fun s -> (mpst thd).get s); put = (fun s v' -> (mpst thd).put s v')}, C
end

[<EntryPoint>]
let main argv = 
    printfn "%A" argv
    0 // return an integer exit code
