using System.Collections.Generic;
using System.Diagnostics.Contracts;

namespace Microsoft.Dafny;

public class ForallExpr : QuantifierExpr, ICloneable<ForallExpr> {
  public override string WhatKind => "forall expression";
  protected override BinaryExpr.ResolvedOpcode SplitResolvedOp => BinaryExpr.ResolvedOpcode.And;

  public ForallExpr(IOrigin origin, List<BoundVar> bvars, Expression range, Expression term, Attributes attrs)
    : base(origin, bvars, range, term, attrs) {
    Contract.Requires(cce.NonNullElements(bvars));
    Contract.Requires(origin != null);
    Contract.Requires(term != null);
  }

  public ForallExpr Clone(Cloner cloner) {
    return new ForallExpr(cloner, this);
  }

  public ForallExpr(Cloner cloner, ForallExpr original) : base(cloner, original) {
  }

  public override Expression LogicalBody(bool bypassSplitQuantifier = false) {
    if (Range == null) {
      return Term;
    }
    var body = new BinaryExpr(Term.Origin, BinaryExpr.Opcode.Imp, Range, Term);
    body.ResolvedOp = BinaryExpr.ResolvedOpcode.Imp;
    body.Type = Term.Type;
    return body;
  }
}