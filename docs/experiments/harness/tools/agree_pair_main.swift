import Foundation
// For app_eval3.py on main. stdin: apple<TAB>whisper. stdout: agreed character count (agreedCharacters).
while let l = readLine() { let p = l.components(separatedBy: "\t"); print(NoWakePolicy.agreedCharacters(p[0], p.count > 1 ? p[1] : "")) }
