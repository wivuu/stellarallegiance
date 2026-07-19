// The one thing TeamStateStore.CheckSpawnGate needs from the def registry: a hull's credit cost. Narrowing
// it to this interface keeps the store free of any dependency on the Godot DefRegistry node, so the store
// is a pure, headless-testable POCO. DefRegistry is the production implementation.
public interface IShipCostSource
{
    // Credits required to spawn hull `cls`; 0 if the def is unknown (defers the gate to the server).
    int ShipCost(byte cls);
}
