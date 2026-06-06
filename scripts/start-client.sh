#!/bin/bash
ARGS=""
if [[ "$1" == "--maincloud" ]]; then
  ARGS="--maincloud"
fi
godot-mono --path client/ $ARGS