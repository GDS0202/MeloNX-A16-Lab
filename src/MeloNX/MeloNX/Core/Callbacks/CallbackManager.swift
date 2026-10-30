//
//  CallbackManager.swift
//  MeloNX
//
//  Created by Stossy11 on 4/4/2026.
//

import Foundation

@dynamicMemberLookup
class CallbackManager {
    private static let registrationLock = NSRecursiveLock()
    private static var registrations: [String: UnsafeMutableRawPointer] = [:]
    
    static subscript(dynamicMember name: String) -> (@escaping (CallbackData) -> Void) -> Void {
        return { handler in
            register(name: name, handler: handler)
        }
    }
    
    static func register(name: String, handler: @escaping (CallbackData) -> Void) {
        registrationLock.lock()
        defer { registrationLock.unlock() }
        let previous = registrations[name]
        let box = CallbackBox(handler)
        let userData = Unmanaged.passRetained(box).toOpaque()
        
        let fn: DataCallbackFn = { rawPtr, userData in
            guard let userData else { return }
            let box = Unmanaged<CallbackBox>.fromOpaque(userData).takeUnretainedValue()
            let data = rawPtr.map { $0.load(as: CallbackData.self) } ?? CallbackData(ptr: nil, len: 0)
            box.body(data)
        }
        
        name.withCString { RegisterCallback($0, fn, userData) }
        registrations[name] = userData
        if let previous { Unmanaged<CallbackBox>.fromOpaque(previous).release() }
    }
    
    static func unregister(name: String) {
        registrationLock.lock()
        defer { registrationLock.unlock() }
        // Native unregister drains the old entry before its Swift box is released.
        name.withCString { UnregisterCallback($0) }
        if let userData = registrations.removeValue(forKey: name) {
            Unmanaged<CallbackBox>.fromOpaque(userData).release()
        }
    }
}
