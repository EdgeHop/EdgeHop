// Page-global helpers. showAlert is invoked from C# by name (no module import), so precise
// matching relies on it being globally unique among the DISCOVERED JS exports.

export function showAlert(message) {
    console.log(message);
}
