package main

import (
	"bufio"
	"context"
	"fmt"
	"os"
	"strings"
	"sync"

	"github.com/msdickinson/vett/sidecar/internal/agent"
	"github.com/msdickinson/vett/sidecar/internal/eventbus"
	"github.com/msdickinson/vett/sidecar/internal/sandbox"
)

// terminalChatLoop runs an interactive chat session in the terminal.
// The user types messages on stdin and sees agent output rendered with
// ANSI colors.
func terminalChatLoop(ctx context.Context, loop *agent.Loop, sb *sandbox.LocalSandbox, bus *eventbus.Bus, systemPrompt, cwd string) error {
	fmt.Printf("\033[1;36mvett chat\033[0m — interactive coding agent\n")
	fmt.Printf("Working directory: %s\n", cwd)
	fmt.Printf("Profile tools: %s\n", toolNamesList(loop))
	fmt.Printf("Type your message and press Enter. Ctrl+C to quit.\n\n")

	// Subscribe to bus events for rendering tool calls.
	events := bus.Subscribe(128, true)
	var renderWg sync.WaitGroup
	renderWg.Add(1)
	go func() {
		defer renderWg.Done()
		renderTerminalEvents(events)
	}()

	// User input channel.
	userCh := make(chan agent.UserInput, 1)

	// Read user input from stdin in a goroutine.
	go func() {
		scanner := bufio.NewScanner(os.Stdin)
		for scanner.Scan() {
			line := scanner.Text()
			if strings.TrimSpace(line) == "" {
				continue
			}
			userCh <- agent.UserInput{Text: line}
		}
		close(userCh)
	}()

	// Run the interactive loop.
	result, err := loop.RunInteractive(ctx, systemPrompt, userCh, agent.InteractiveCallbacks{
		OnAssistantText: func(text string) {
			fmt.Printf("\n\033[1;32massistant:\033[0m %s\n", text)
		},
		OnWaitingForInput: func() {
			fmt.Printf("\n\033[1;33myou:\033[0m ")
		},
	})

	// Close the bus to drain the event goroutine, then wait for it.
	bus.Close()
	renderWg.Wait()

	if err != nil && ctx.Err() == nil {
		return fmt.Errorf("chat loop: %w", err)
	}

	fmt.Printf("\n\033[1;36m--- session ended ---\033[0m\n")
	if result != nil {
		fmt.Printf("Iterations: %d | Tokens: %d in / %d out | Reason: %s\n",
			result.Iterations, result.InputTokens, result.OutputTokens, result.StopReason)
	}
	return nil
}

// renderTerminalEvents reads from the event bus and prints tool call
// activity to the terminal.
func renderTerminalEvents(events <-chan eventbus.Event) {
	for e := range events {
		switch e.Type {
		case eventbus.ToolCallStart:
			name, _ := e.Data["tool_name"].(string)
			fmt.Printf("  \033[0;34m▶ %s\033[0m", name)
			if args, ok := e.Data["arguments"].(map[string]any); ok {
				if cmd, ok := args["command"].(string); ok {
					short := cmd
					if len(short) > 80 {
						short = short[:77] + "..."
					}
					fmt.Printf(": %s", short)
				} else if command, ok := args["command_name"].(string); ok {
					fmt.Printf(": %s", command)
					if path, ok := args["path"].(string); ok {
						fmt.Printf(" %s", path)
					}
				}
			}
			fmt.Println()
		case eventbus.ToolCallEnd:
			name, _ := e.Data["tool_name"].(string)
			success, _ := e.Data["success"].(bool)
			dur, _ := e.Data["duration_ms"].(int64)
			if success {
				fmt.Printf("  \033[0;32m✓ %s\033[0m (%dms)\n", name, dur)
			} else {
				fmt.Printf("  \033[0;31m✗ %s\033[0m (%dms)\n", name, dur)
			}
		}
	}
}

func toolNamesList(loop *agent.Loop) string {
	names := make([]string, 0, len(loop.Tools))
	for name := range loop.Tools {
		names = append(names, name)
	}
	return strings.Join(names, ", ")
}
