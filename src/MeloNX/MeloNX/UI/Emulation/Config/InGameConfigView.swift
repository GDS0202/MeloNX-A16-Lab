//
//  InGameConfigView.swift
//  MeloNX
//
//  Created by Stossy11 on 7/7/2026.
//

import SwiftUI
import Melo_Controller
import Combine

@MainActor
class InGameConfigView_MenuView: ObservableObject {
    @Published var showButton = true
    static let shared = InGameConfigView_MenuView()
    func triggerButton() { if !showButton { showButton = true } }
}

struct InGameConfigView: View {
    @EnvironmentObject var ryujinxController: RyujinxController
    @ObservedObject var controllerManager: ControllerManager = .shared
    @StateObject var menuViewHandler: InGameConfigView_MenuView = .shared
    @State var rotationlock: Bool = false
    @State var showingKeyboardConfig: Bool = false
    @State var showControllerSettings: Bool = false
    @State private var showGameSettings = false
    @ObservedObject private var nativeSettings = NativeSettingsManager.shared
    let titleId: String
    

    
    var body: some View {
        Menu {
            menuButton
        } label: {
            Image(systemName: "line.3.horizontal.circle.fill")
                .font(.system(size: 30))
                .foregroundStyle(.white)
                .frame(width: 48, height: 48)
                .background(.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 12))
                .contentShape(Rectangle())
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .padding(12)
        .accessibilityLabel("Game menu")
        .disabled(ryujinxController.isStopping)
        .sheet(isPresented: $showGameSettings) {
            PerGameSettingsView(titleId)
                .environmentObject(ryujinxController)
        }
        .sheet(isPresented: $showControllerSettings) {
            controllerSelection
        }
    }
    
    
    @ViewBuilder
    private var menuButton: some View {
        Button {
            ryujinxController.isPaused.toggle()
            ryujinxController.wasManuallyPaused = ryujinxController.isPaused
        } label: {
            Label {
                Text(ryujinxController.isPaused ? "Play" : "Pause")
            } icon: {
                Image(systemName: ryujinxController.isPaused ? "play.circle" : "pause.circle")
            }
        }
        
        Button {
            showControllerSettings = true
        } label: {
            Label {
                Text("Configure Controllers")
            } icon: {
                Image(systemName: "gamecontroller")
            }
        }
        
        if UIDevice.current.userInterfaceIdiom == .phone {
            Button {
                rotationlock.toggle()
                if rotationlock {
                    OrientationManager.lockCurrentOrientation(UIDevice.current.orientation)
                } else {
                    OrientationManager.lockOrientation(.all, rotateTo: UIDevice.current.orientation)
                }
            } label: {
                Label {
                    Text("Rotation Lock")
                } icon: {
                    Image(systemName: rotationlock ? "lock" : "lock.open")
                }
            }
        }
        
        
        Button {
            showGameSettings = true
        } label: { Label("Game settings", systemImage: "slider.horizontal.3") }

        Toggle(isOn: nativeSettings.performacehud(false).projectedValue) {
            Label("Performance information", systemImage: "gauge.with.needle")
        }

        Button(role: .destructive) {
            ryujinxController.returnToLibrary()
        } label: {
            Label("Return to library", systemImage: "house")
        }
         
    }
    
    @ViewBuilder
    private var controllerSelection: some View {
        List {
            Section("Controller Selection") {
                if controllerManager.selectedControllers.isEmpty {
                    Text("No controllers selected, keyboard will be used")
                        .foregroundColor(.secondary)
                } else {
                    ForEach(Array(controllerManager.resolvedSelection.enumerated()), id: \.offset) { index, entry in
                        ControllerRow(index: index, controllerId: entry.id, controllerManager: controllerManager)
                    }
                }
                if hasAvailableControllers {
                    Menu {
                        ForEach(controllerManager.allControllers.filter {
                            !contains(controllerManager.selectedControllers, value: $0)
                        }) { controller in
                            Button(controller.name) {
                                controllerManager.selectedControllers.append(controller.id)
                            }
                        }
                    } label: {
                        Label("Add Controller", systemImage: "plus.circle.fill")
                    }
                }
            }
            
            
            if controllerManager.selectedControllers.isEmpty {
                Section {
                    Button("Map Keyboard") {
                        showingKeyboardConfig = true
                    }
                    .sheet(isPresented: $showingKeyboardConfig) {
                        KeyboardConfigView()
                    }
                }
            }
        }
        .padding(.top)
        .onAppear {
            controllerManager.refreshControllersList()
        }
        .onDisappear {
            controllerManager.refreshControllersList(true)
        }
    }
    
    private var hasAvailableControllers: Bool {
        !ControllerManager.shared.allControllers
            .filter { !contains(ControllerManager.shared.selectedControllers, value: $0) }
            .isEmpty
    }
    
    func contains(_ array: [String], value: BaseController) -> Bool {
        array.contains { $0 == value.id }
    }
}
