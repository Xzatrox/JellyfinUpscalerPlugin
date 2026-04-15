#!/usr/bin/env python3
"""
Frame Pipe Helper for FFmpeg Wrapper
Reads raw video frames from stdin/named pipe, POSTs each frame to Docker AI service,
and writes upscaled frames to output pipe.
"""

import sys
import argparse
import requests
import io
from PIL import Image
import struct
import logging

logging.basicConfig(level=logging.INFO, format='[%(asctime)s] %(levelname)s: %(message)s')
logger = logging.getLogger(__name__)


def read_raw_frame(input_stream, width, height, pix_fmt='rgb24'):
    """Read a single raw video frame from the input stream."""
    if pix_fmt == 'rgb24':
        bytes_per_pixel = 3
    else:
        raise ValueError(f"Unsupported pixel format: {pix_fmt}")
    
    frame_size = width * height * bytes_per_pixel
    frame_data = input_stream.read(frame_size)
    
    if len(frame_data) < frame_size:
        return None  # End of stream or incomplete frame
    
    return frame_data


def raw_to_jpeg(frame_data, width, height):
    """Convert raw RGB24 frame data to JPEG bytes."""
    try:
        img = Image.frombytes('RGB', (width, height), frame_data)
        jpeg_buffer = io.BytesIO()
        img.save(jpeg_buffer, format='JPEG', quality=95)
        return jpeg_buffer.getvalue()
    except Exception as e:
        logger.error(f"Failed to convert frame to JPEG: {e}")
        return None


def upscale_frame(jpeg_data, ai_url, api_token):
    """POST JPEG frame to Docker AI service and return upscaled JPEG."""
    try:
        headers = {
            'X-Api-Token': api_token,
            'Content-Type': 'image/jpeg'
        }
        
        response = requests.post(
            f"{ai_url}/upscale-frame",
            data=jpeg_data,
            headers=headers,
            timeout=5.0  # 5 second timeout for real-time processing
        )
        
        if response.status_code == 200:
            return response.content
        else:
            logger.warning(f"AI service returned status {response.status_code}")
            return None
    except requests.exceptions.Timeout:
        logger.warning("AI service timeout - frame dropped")
        return None
    except Exception as e:
        logger.error(f"Failed to upscale frame: {e}")
        return None


def jpeg_to_raw(jpeg_data, target_width, target_height):
    """Convert JPEG bytes back to raw RGB24 frame data."""
    try:
        img = Image.open(io.BytesIO(jpeg_data))
        
        # Resize if needed to match target dimensions
        if img.size != (target_width, target_height):
            img = img.resize((target_width, target_height), Image.LANCZOS)
        
        # Convert to RGB if needed
        if img.mode != 'RGB':
            img = img.convert('RGB')
        
        return img.tobytes()
    except Exception as e:
        logger.error(f"Failed to convert JPEG to raw: {e}")
        return None


def main():
    parser = argparse.ArgumentParser(description='Frame pipe helper for FFmpeg wrapper')
    parser.add_argument('--input', required=True, help='Input pipe path or "-" for stdin')
    parser.add_argument('--output', required=True, help='Output pipe path or "-" for stdout')
    parser.add_argument('--ai-url', required=True, help='Docker AI service URL')
    parser.add_argument('--token', required=True, help='API token for AI service')
    parser.add_argument('--width', type=int, required=True, help='Input frame width')
    parser.add_argument('--height', type=int, required=True, help='Input frame height')
    parser.add_argument('--scale', type=int, default=2, help='Upscale factor (default: 2)')
    parser.add_argument('--drop-on-slow', action='store_true', 
                        help='Drop frames and pass original if AI is too slow')
    
    args = parser.parse_args()
    
    # Open input stream
    if args.input == '-':
        input_stream = sys.stdin.buffer
    else:
        input_stream = open(args.input, 'rb')
    
    # Open output stream
    if args.output == '-':
        output_stream = sys.stdout.buffer
    else:
        output_stream = open(args.output, 'wb')
    
    target_width = args.width * args.scale
    target_height = args.height * args.scale
    
    frame_count = 0
    dropped_count = 0
    
    logger.info(f"Starting frame pipe: {args.width}x{args.height} -> {target_width}x{target_height}")
    logger.info(f"AI Service: {args.ai_url}")
    
    try:
        while True:
            # Read raw frame
            frame_data = read_raw_frame(input_stream, args.width, args.height)
            if frame_data is None:
                break  # End of stream
            
            frame_count += 1
            
            # Convert to JPEG
            jpeg_data = raw_to_jpeg(frame_data, args.width, args.height)
            if jpeg_data is None:
                logger.warning(f"Frame {frame_count}: Failed to encode JPEG, passing original")
                # Write original frame upscaled via simple resize
                img = Image.frombytes('RGB', (args.width, args.height), frame_data)
                img = img.resize((target_width, target_height), Image.LANCZOS)
                output_stream.write(img.tobytes())
                continue
            
            # Upscale via AI service
            upscaled_jpeg = upscale_frame(jpeg_data, args.ai_url, args.token)
            
            if upscaled_jpeg is None:
                dropped_count += 1
                if args.drop_on_slow:
                    # Pass through original frame with simple upscale
                    logger.debug(f"Frame {frame_count}: AI slow/failed, using fallback upscale")
                    img = Image.frombytes('RGB', (args.width, args.height), frame_data)
                    img = img.resize((target_width, target_height), Image.LANCZOS)
                    output_stream.write(img.tobytes())
                else:
                    # Retry once
                    upscaled_jpeg = upscale_frame(jpeg_data, args.ai_url, args.token)
                    if upscaled_jpeg is None:
                        logger.warning(f"Frame {frame_count}: AI failed after retry, using fallback")
                        img = Image.frombytes('RGB', (args.width, args.height), frame_data)
                        img = img.resize((target_width, target_height), Image.LANCZOS)
                        output_stream.write(img.tobytes())
                        continue
            
            # Convert back to raw and write
            raw_upscaled = jpeg_to_raw(upscaled_jpeg, target_width, target_height)
            if raw_upscaled is None:
                logger.warning(f"Frame {frame_count}: Failed to decode upscaled frame")
                continue
            
            output_stream.write(raw_upscaled)
            output_stream.flush()
            
            if frame_count % 100 == 0:
                logger.info(f"Processed {frame_count} frames ({dropped_count} dropped/fallback)")
    
    except KeyboardInterrupt:
        logger.info("Interrupted by user")
    except Exception as e:
        logger.error(f"Fatal error: {e}", exc_info=True)
        return 1
    finally:
        if args.input != '-':
            input_stream.close()
        if args.output != '-':
            output_stream.close()
        
        logger.info(f"Finished: {frame_count} frames processed, {dropped_count} dropped/fallback")
    
    return 0


if __name__ == '__main__':
    sys.exit(main())
