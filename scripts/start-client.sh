#!/bin/bash
ARGS=""
if [[ "$1" == "--maincloud" ]]; then
  ARGS="--maincloud"
fi
dotnet build client/ -c Release
godot-mono --path client/ $ARGS