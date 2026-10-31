const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

// Exercise the production capture function without a provider or user browser.
// The fixture models native labels and ARIA groups, not any site's class names.
const source = fs.readFileSync(path.join(__dirname, "..", "content-script.js"), "utf8");
const fieldFunction = source.slice(source.indexOf("  function formField("), source.indexOf("  function referencedLabelText("));
class Element {
  constructor(role, attrs = {}) { this.role = role; this.attrs = attrs; this.members = []; }
  getAttribute(name) { return this.attrs[name] || null; }
  closest(selector) { return selector.includes("radiogroup") ? this.group : this.question; }
  querySelectorAll() { return this.members; }
  querySelector() { return null; }
}
class Input extends Element {}
const labels = new Map([
  ["quality", { textContent: "How would you rate the quality?" }],
  ["usefulness", { textContent: "How useful is it?" }],
]);
const scope = vm.createContext({
  WeakMap, HTMLInputElement: Input,
  document: { getElementById: id => labels.get(id), getElementsByName: () => [] },
  roleOf: el => el.role,
  accessibleName: el => el.getAttribute("aria-label") || "",
  safeControlValue: el => el.value || "",
  visibleText: el => el?.textContent || "",
  referencedLabelText: el => el?.textContent || "",
  truncate: (s, n) => s.slice(0, n),
  domEpoch: "test-document", hashString: s => s,
});
vm.runInContext(`const fieldIds = new WeakMap(); let nextFieldId = 0; let fieldFrameId = 0; ${fieldFunction}`, scope);
const capture = el => { scope.element = el; return vm.runInContext("formField(element)", scope); };
function choice(labelId, required = true) {
  const group = new Element("radiogroup", { "aria-labelledby": labelId, "aria-required": String(required) });
  const radio = new Element("radio", { "aria-label": "4", "aria-checked": "false" });
  radio.group = group;
  group.members = [radio];
  return radio;
}
const quality = choice("quality");
const usefulness = choice("usefulness");
assert.equal(capture(quality).fieldLabel, "How would you rate the quality?");
assert.equal(capture(usefulness).fieldLabel, "How useful is it?");
assert.notEqual(capture(quality).fieldId, capture(usefulness).fieldId);
assert.equal(capture(quality).fieldRequired, true);
assert.equal(capture(quality).fieldAnswered, false);
usefulness.attrs["aria-checked"] = "true";
assert.equal(capture(usefulness).fieldAnswered, true);
assert.equal(capture(quality).fieldAnswered, false);
const optional = choice("quality", false);
assert.equal(capture(optional).fieldRequired, false);
const input = new Input("textbox", { "aria-label": "Reference code" });
input.required = true;
assert.equal(capture(input).fieldRequired, true);
assert.equal(capture(input).fieldAnswered, false);
input.value = "REF-42";
assert.equal(capture(input).fieldAnswered, true);
assert.equal(capture(new Element("textbox", { "aria-label": "Unknown policy" })).fieldRequired, null);
const requiredCheckbox = new Input("checkbox", { "aria-label": "Required consent" });
requiredCheckbox.required = true;
requiredCheckbox.group = new Element("group");
requiredCheckbox.group.members = [requiredCheckbox, new Element("checkbox", { "aria-checked": "true" })];
assert.equal(capture(requiredCheckbox).fieldRequired, true);
assert.equal(capture(requiredCheckbox).fieldAnswered, false);
assert.equal(capture(new Element("button")).fieldId, undefined);
scope.element = quality;
assert.match(vm.runInContext("formField(element, 9).fieldId", scope), /^9:/);
console.log("Form context: question identity, repeated options, required/optional, answer state and frame scope passed.");

// Native option labels and stored values are separate pieces of evidence.
class Select {
  constructor(options) { this.options = options; this.value = options[0].value; }
  get selectedOptions() { return this.options.filter(o => o.value === this.value); }
  dispatchEvent() {}
}
const optionsScope = vm.createContext({ HTMLSelectElement: Select, Event: class {},
  normalize: s => String(s || "").trim().toLowerCase(), isRestrictedInput: () => false });
vm.runInContext(source.slice(source.indexOf("  function selectOption("), source.indexOf("  function findSemanticControl("))
  + source.slice(source.indexOf("  function valueMatches("), source.indexOf("  function valueContains(")), optionsScope);
optionsScope.element = new Select([{value:"1", textContent:"Alpha"}, {value:"2", textContent:"Beta"}]);
vm.runInContext('selectOption(element, "Beta")', optionsScope);
assert.equal(optionsScope.element.value, "2");
assert.equal(vm.runInContext('valueMatches(element, "Beta")', optionsScope), true);
assert.equal(vm.runInContext('valueMatches(element, "2")', optionsScope), true);
assert.equal(vm.runInContext('valueMatches(element, "Alpha")', optionsScope), false);
assert.throws(() => vm.runInContext('selectOption(element, "Be")', optionsScope), /missing or ambiguous/);
optionsScope.element.options.push({value:"3", textContent:"Beta"});
assert.throws(() => vm.runInContext('selectOption(element, "Beta")', optionsScope), /ambiguous/);
assert.equal(optionsScope.element.value, "2");
console.log("Native select: exact label/value, unselected options and ambiguous targets passed.");

const identityScope = vm.createContext({ normalize: s => String(s || "").trim().toLowerCase(),
  formField: element => ({fieldId: element.fieldId}), semanticIdentity: element => element.identity });
vm.runInContext(source.slice(source.indexOf("  function semanticScore("), source.indexOf("  function semanticIdentity(")), identityScope);
identityScope.element = {fieldId:"exact-field", identity:{role:"textbox",name:""},
  matches:()=>true, getBoundingClientRect:()=>({width:100,height:25}), getAttribute:()=>null};
const match = command => { identityScope.command = command; return vm.runInContext("semanticMatch(element, command)", identityScope); };
assert.equal(match({targetRole:"textbox",targetFieldId:"exact-field"}), true,
  "fresh exact anonymous field must not depend on a fuzzy-name score");
assert.equal(match({targetRole:"textbox",targetFieldId:"other-field"}), false);
assert.equal(match({targetRole:"checkbox",targetFieldId:"exact-field"}), false);
assert.equal(match({targetRole:"textbox",targetFieldId:"exact-field",targetName:"Invented label"}), false);
console.log("Exact anonymous control identity: field/role/name checks passed.");

{
const labelScope = vm.createContext({ visibleText: node => String(node.textContent || "").trim(),
  truncate: (s,n)=>s.slice(0,n) });
vm.runInContext(source.slice(source.indexOf("  function contextualAccessibleLabel("), source.indexOf("  function safeActions(")), labelScope);
const text = value => ({nodeType:3,textContent:value});
const newline = {nodeType:1,tagName:"BR"};
const input = {closest:()=>null, matches:selector=>selector==='input,textarea,select', previousSibling:text(' ')};
input.previousSibling.previousSibling = newline;
newline.previousSibling = text('Project title:');
labelScope.element = input;
assert.equal(vm.runInContext('contextualAccessibleLabel(element)',labelScope),'Project title:');
input.previousSibling = {nodeType:1,tagName:'INPUT',textContent:'another field'};
assert.equal(vm.runInContext('contextualAccessibleLabel(element)',labelScope),'');
input.matches = ()=>true;input.nextSibling=text('Choice Delta');
assert.equal(vm.runInContext('contextualAccessibleLabel(element)',labelScope),'Choice Delta');
input.closest=()=>({getAttribute:()=> 'ARIA parent'});
assert.equal(vm.runInContext('contextualAccessibleLabel(element)',labelScope),'ARIA parent');
console.log("Legacy form labels: bounded adjacent text, choice text, no cross-control borrowing, ARIA precedence passed.");
}

// A button can have a value property but cannot receive an input's native
// setter. A failed model target must be rejected before focus or mutation.
{
class TextInput {
  constructor(type = "text") { this.type = type; this._value = "before"; this.focusCount = 0; this.events = 0; }
  get value() { return this._value; }
  set value(value) { if (!(this instanceof TextInput)) throw new TypeError("Illegal invocation"); this._value = value; }
  focus() { this.focusCount++; }
  dispatchEvent() { this.events++; }
}
class TextArea extends TextInput {}
const editingScope = vm.createContext({
  HTMLInputElement: TextInput, HTMLTextAreaElement: TextArea,
  Event: class {}, InputEvent: class {},
});
vm.runInContext(source.slice(source.indexOf("  function setEditableValue("), source.indexOf("  function normalizeNativeInputValue("))
  + source.slice(source.indexOf("  function isEditable("), source.indexOf("  function isRestrictedInput("))
  + "function normalizeNativeInputValue(element, text) { return text; }", editingScope);
const edit = element => { editingScope.element = element; return vm.runInContext('setEditableValue(element, "after", true)', editingScope); };
const button = { value: "before", focusCount: 0, focus() { this.focusCount++; }, dispatchEvent() {} };
for (const element of [button, ...["button", "submit", "checkbox", "radio", "file", "password", "hidden"].map(t => new TextInput(t)),
  Object.assign(new TextInput(), { readOnly: true }), Object.assign(new TextInput(), { disabled: true })]) {
  assert.throws(() => edit(element), /not an editable text control/);
  assert.equal(element.value, "before");
  assert.equal(element.focusCount, 0);
}
for (const element of [new TextInput(), new TextInput("search"), new TextInput("date"), new TextArea()]) {
  edit(element);
  assert.equal(element.value, "after");
  assert.equal(element.focusCount, 1);
  assert.equal(element.events, 2);
}
console.log("Typing target: buttons/non-text/read-only/disabled rejected before mutation; text/search/date/textarea preserved.");
}
